using System.Diagnostics;
using UnBramble.Cli.Defender;
using UnBramble.Core.Config;
using UnBramble.Core.Monitoring;

namespace UnBramble.Tests;

/// <summary>
/// Windows-Defender-exclusion setup (<c>DefenderExclusionSetup</c>/<c>DefenderApply</c>/
/// <c>DefenderEligibilityCheck</c>/<c>DefenderDriftDetector</c>): every Defender-facing operation
/// (the non-elevated status PowerShell, the tamper-protection registry read, `fsutil devdrv query`,
/// the single elevated `powershell.exe` hop) sits behind a small interface
/// (<see cref="IPowerShellRunner"/>, <see cref="IDefenderRegistryReader"/>,
/// <see cref="IDevDriveChecker"/>, <see cref="IElevatedPowerShellRunner"/>), faked here -- this
/// suite never shells out to a real `powershell.exe`/`fsutil.exe`, never touches the real registry,
/// and never triggers a real UAC prompt or modifies this machine's actual Defender exclusions.
/// </summary>
public class DefenderExclusionSetupTests
{
    // ---- Fakes -------------------------------------------------------------------------------

    private sealed class FakePowerShellRunner(Func<string, PowerShellResult> handler) : IPowerShellRunner
    {
        public List<string> Scripts { get; } = [];

        public PowerShellResult Run(string script)
        {
            Scripts.Add(script);
            return handler(script);
        }

        public static FakePowerShellRunner ReturningStatus(string amRunningMode, bool realTimeProtectionEnabled) =>
            new(_ => new PowerShellResult(
                0,
                $$"""{"AMRunningMode":"{{amRunningMode}}","RealTimeProtectionEnabled":{{(realTimeProtectionEnabled ? "true" : "false")}}}""",
                ""));

        public static FakePowerShellRunner ModuleMissing() => new(_ => new PowerShellResult(1, "", "Get-MpComputerStatus is not recognized"));
    }

    private sealed class FakeRegistryReader(bool tamperProtected) : IDefenderRegistryReader
    {
        public bool ExclusionsAreTamperProtected() => tamperProtected;
    }

    private sealed class FakeDevDriveChecker(bool isDevDrive) : IDevDriveChecker
    {
        public bool IsDevDrive(string path) => isDevDrive;
    }

    /// <summary>Simulates the single elevated `powershell.exe` hop entirely in-process: reads the
    /// plan file the caller wrote (at this project root's well-known path), computes results via
    /// <paramref name="simulate"/>, and writes the result file itself -- exactly what the real
    /// elevated PowerShell script does, minus any real UAC prompt or Defender call. Constructed with
    /// the project root because, unlike the old two-hop design, the elevated launch no longer carries
    /// plan/result paths in a command line to parse -- they're baked (as PS literals) into the opaque
    /// `-EncodedCommand` script, so the fake resolves them the same way production does: from the
    /// project root via <see cref="DefenderPlanFile.PathFor"/>/<see cref="DefenderResultFile.PathFor"/>.</summary>
    private sealed class SimulatedElevator(string projectRoot, Func<IReadOnlyList<DefenderEntry>, List<DefenderEntryResult>> simulate) : IElevatedPowerShellRunner
    {
        public List<string> Scripts { get; } = [];

        public ElevatedLaunchResult RunElevated(string script)
        {
            Scripts.Add(script);
            var entries = DefenderPlanFile.TryRead(DefenderPlanFile.PathFor(projectRoot)) ?? [];
            var results = simulate(entries);
            var success = results.All(r => !DefenderDecisionText.IsFailure(r.Outcome));
            DefenderResultFile.Write(DefenderResultFile.PathFor(projectRoot), success, results);
            return new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, success ? 0 : 1, null);
        }
    }

    private sealed class FixedElevator(ElevatedLaunchResult result) : IElevatedPowerShellRunner
    {
        public List<string> Scripts { get; } = [];

        public ElevatedLaunchResult RunElevated(string script)
        {
            Scripts.Add(script);
            return result;
        }
    }

    private static DefenderExclusionSetup.Dependencies HealthyDeps(
        IElevatedPowerShellRunner elevator, string exePath = @"C:\tools\unbramble.exe") =>
        new(FakePowerShellRunner.ReturningStatus("Normal", true), new FakeRegistryReader(false), new FakeDevDriveChecker(false), elevator, exePath);

    private static Func<string?> Answers(params string?[] answers)
    {
        var queue = new Queue<string?>(answers);
        return () => queue.Count > 0 ? queue.Dequeue() : null;
    }

    private static (List<string> Lines, Action<string> Announce) Recorder()
    {
        var lines = new List<string>();
        return (lines, lines.Add);
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "unbramble-defender-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- Eligibility matrix -------------------------------------------------------------------

    [Fact]
    public void Eligibility_NotWindows_SkipsSilently()
    {
        var result = DefenderEligibilityCheck.Evaluate(isWindows: false, isDevDrive: false, status: null, exclusionsTamperProtected: false);
        Assert.Equal(DefenderEligibility.SkippedNotWindows, result.Status);
        Assert.False(result.ShouldProceed);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Eligibility_DevDrive_SkipsWithNote_RegardlessOfDefenderStatus()
    {
        var result = DefenderEligibilityCheck.Evaluate(
            isWindows: true, isDevDrive: true, status: new DefenderRuntimeStatus("Normal", true), exclusionsTamperProtected: true);

        Assert.Equal(DefenderEligibility.SkippedDevDrive, result.Status);
        Assert.False(result.ShouldProceed);
        Assert.Contains("Dev Drive", result.Note);
    }

    [Fact]
    public void Eligibility_NoDefenderModule_SkipsSilently()
    {
        var result = DefenderEligibilityCheck.Evaluate(isWindows: true, isDevDrive: false, status: null, exclusionsTamperProtected: false);
        Assert.Equal(DefenderEligibility.SkippedNoDefenderModule, result.Status);
        Assert.Null(result.Note);
    }

    [Theory]
    [InlineData("Passive", true)]
    [InlineData("Normal", false)]
    public void Eligibility_ThirdPartyAvOrRtpDisabled_SkipsWithNote(string mode, bool rtpEnabled)
    {
        var result = DefenderEligibilityCheck.Evaluate(
            isWindows: true, isDevDrive: false, status: new DefenderRuntimeStatus(mode, rtpEnabled), exclusionsTamperProtected: false);

        Assert.Equal(DefenderEligibility.SkippedThirdPartyAv, result.Status);
        Assert.Contains("third-party", result.Note);
    }

    [Fact]
    public void Eligibility_TamperProtected_SkipsWithNote()
    {
        var result = DefenderEligibilityCheck.Evaluate(
            isWindows: true, isDevDrive: false, status: new DefenderRuntimeStatus("Normal", true), exclusionsTamperProtected: true);

        Assert.Equal(DefenderEligibility.SkippedManaged, result.Status);
        Assert.Contains("managed", result.Note);
    }

    [Fact]
    public void Eligibility_EverythingHealthy_Proceeds()
    {
        var result = DefenderEligibilityCheck.Evaluate(
            isWindows: true, isDevDrive: false, status: new DefenderRuntimeStatus("Normal", true), exclusionsTamperProtected: false);

        Assert.Equal(DefenderEligibility.Proceed, result.Status);
        Assert.True(result.ShouldProceed);
    }

    // ---- DefenderStatusReader --------------------------------------------------------------

    [Fact]
    public void StatusReader_ParsesRunningModeAndRtp()
    {
        var runner = FakePowerShellRunner.ReturningStatus("Normal", true);
        var status = DefenderStatusReader.TryGetStatus(runner);
        Assert.NotNull(status);
        Assert.Equal("Normal", status.Value.AmRunningMode);
        Assert.True(status.Value.RealTimeProtectionEnabled);
    }

    [Fact]
    public void StatusReader_NonZeroExit_ReturnsNull()
    {
        var status = DefenderStatusReader.TryGetStatus(FakePowerShellRunner.ModuleMissing());
        Assert.Null(status);
    }

    [Fact]
    public void StatusReader_UnparseableOutput_ReturnsNull()
    {
        var runner = new FakePowerShellRunner(_ => new PowerShellResult(0, "not json", ""));
        Assert.Null(DefenderStatusReader.TryGetStatus(runner));
    }

    // ---- ComputeDesiredEntries / ComputeDelta -----------------------------------------------

    [Fact]
    public void ComputeDesiredEntries_IncludesProcessAndRootPath()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var entries = DefenderExclusionSetup.ComputeDesiredEntries(root, config, @"C:\tools\unbramble.exe");

            Assert.Contains(entries, e => e.Type == DefenderEntryType.Process && e.Value == @"C:\tools\unbramble.exe");
            Assert.Contains(entries, e => e.Type == DefenderEntryType.Path && string.Equals(e.Value, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ComputeDesiredEntries_JunctionTargetOutsideRoot_IsIncluded()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var external = Path.Combine(Path.GetTempPath(), "unbramble-defender-external-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(external);
            try
            {
                CreateJunction(Path.Combine(root, "Assets", "Vendor"), external);

                var config = new UnBrambleConfig { Roots = ["Assets"] };
                var entries = DefenderExclusionSetup.ComputeDesiredEntries(root, config, @"C:\tools\unbramble.exe");

                Assert.Contains(entries, e => e.Type == DefenderEntryType.Path && string.Equals(e.Value, Path.GetFullPath(external), StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                TryDelete(external);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ComputeDelta_NoState_ReturnsEverything()
    {
        var desired = new List<DefenderEntry> { new(DefenderEntryType.Process, "exe"), new(DefenderEntryType.Path, "root") };
        var delta = DefenderExclusionSetup.ComputeDelta(desired, null);
        Assert.Equal(2, delta.Count);
    }

    [Fact]
    public void ComputeDelta_SomeAlreadyCovered_ReturnsOnlyMissing()
    {
        var desired = new List<DefenderEntry> { new(DefenderEntryType.Process, "exe"), new(DefenderEntryType.Path, "root"), new(DefenderEntryType.Path, "new-target") };
        var state = new DefenderStateFileJson
        {
            Decision = "applied",
            Entries =
            [
                new DefenderStateEntryJson { Type = "process", Value = "exe", Outcome = "added" },
                new DefenderStateEntryJson { Type = "path", Value = "root", Outcome = "already-covered" },
            ],
        };

        var delta = DefenderExclusionSetup.ComputeDelta(desired, state);
        var single = Assert.Single(delta);
        Assert.Equal("new-target", single.Value);
    }

    [Fact]
    public void ComputeDelta_PreviouslyBlockedOrErrored_IsRetried()
    {
        var desired = new List<DefenderEntry> { new(DefenderEntryType.Path, "root") };
        var state = new DefenderStateFileJson
        {
            Decision = "blocked",
            Entries = [new DefenderStateEntryJson { Type = "path", Value = "root", Outcome = "blocked" }],
        };

        var delta = DefenderExclusionSetup.ComputeDelta(desired, state);
        Assert.Single(delta);
    }

    // ---- Consent-flow state transitions ------------------------------------------------------

    [Fact]
    public void MaybeOfferSetup_NonInteractive_PrintsHint_NeverPromptsOrElevates()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var (lines, announce) = Recorder();
            var elevator = new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, 0, null));

            DefenderExclusionSetup.MaybeOfferSetup(
                root, new UnBrambleConfig { Roots = ["Assets"] }, HealthyDeps(elevator),
                interactive: false, forcePrompt: false, skip: false, announce, Answers());

            Assert.Contains(lines, l => l.Contains("interactive terminal", StringComparison.Ordinal));
            Assert.Empty(elevator.Scripts);
            Assert.Null(DefenderStateFile.TryRead(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MaybeOfferSetup_Skip_DoesNothing()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var (lines, announce) = Recorder();
            var elevator = new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, 0, null));

            DefenderExclusionSetup.MaybeOfferSetup(
                root, new UnBrambleConfig { Roots = ["Assets"] }, HealthyDeps(elevator),
                interactive: true, forcePrompt: false, skip: true, announce, Answers("y"));

            Assert.Empty(lines);
            Assert.Empty(elevator.Scripts);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MaybeOfferSetup_Interactive_Accept_ElevatesAndRecordsApplied()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var (lines, announce) = Recorder();
            var elevator = new SimulatedElevator(root, entries => [.. entries.Select(e => new DefenderEntryResult(e, DefenderEntryOutcome.Added))]);

            DefenderExclusionSetup.MaybeOfferSetup(
                root, new UnBrambleConfig { Roots = ["Assets"] }, HealthyDeps(elevator),
                interactive: true, forcePrompt: false, skip: false, announce, Answers("y"));

            Assert.Single(elevator.Scripts);
            Assert.Contains(lines, l => l.Contains("Add these exclusions?", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("done", StringComparison.OrdinalIgnoreCase));

            var state = DefenderStateFile.TryRead(root);
            Assert.NotNull(state);
            Assert.Equal("applied", state!.Decision);
            Assert.True(state.Entries.Count >= 2); // process + root path, at minimum
            Assert.All(state.Entries, e => Assert.Equal("added", e.Outcome));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("anything else")]
    public void MaybeOfferSetup_Interactive_Decline_RecordsDeclinedAndNeverElevates(string answer)
    {
        var root = CreateTempProjectRoot();
        try
        {
            var (lines, announce) = Recorder();
            var elevator = new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, 0, null));

            DefenderExclusionSetup.MaybeOfferSetup(
                root, new UnBrambleConfig { Roots = ["Assets"] }, HealthyDeps(elevator),
                interactive: true, forcePrompt: false, skip: false, announce, Answers(answer));

            Assert.Empty(elevator.Scripts);
            Assert.Contains(lines, l => l.Contains("Skipped", StringComparison.Ordinal));

            var state = DefenderStateFile.TryRead(root);
            Assert.NotNull(state);
            Assert.Equal("declined", state!.Decision);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MaybeOfferSetup_AfterDecline_PlainInitNeverReprompts()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var config = new UnBrambleConfig { Roots = ["Assets"] };
            DefenderStateFile.Write(root, DefenderDecision.Declined, @"C:\tools\unbramble.exe", []);

            var (lines, announce) = Recorder();
            var elevator = new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, 0, null));
            var readLineCalled = false;
            Func<string?> readLine = () => { readLineCalled = true; return "y"; };

            DefenderExclusionSetup.MaybeOfferSetup(
                root, config, HealthyDeps(elevator), interactive: true, forcePrompt: false, skip: false, announce, readLine);

            Assert.False(readLineCalled, "a plain init must never re-prompt after an explicit decline");
            Assert.Empty(elevator.Scripts);
            Assert.Empty(lines);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void RunSetup_AfterDecline_ForcesFreshPrompt()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var config = new UnBrambleConfig { Roots = ["Assets"] };
            DefenderStateFile.Write(root, DefenderDecision.Declined, @"C:\tools\unbramble.exe", []);

            var (lines, announce) = Recorder();
            var elevator = new SimulatedElevator(root, entries => [.. entries.Select(e => new DefenderEntryResult(e, DefenderEntryOutcome.Added))]);

            DefenderExclusionSetup.RunSetup(root, config, HealthyDeps(elevator), interactive: true, announce, Answers("yes"));

            Assert.Single(elevator.Scripts);
            var state = DefenderStateFile.TryRead(root);
            Assert.Equal("applied", state!.Decision);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MaybeOfferSetup_AlreadyUpToDate_NoPromptNoElevation()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var exePath = @"C:\tools\unbramble.exe";
            var desired = DefenderExclusionSetup.ComputeDesiredEntries(root, config, exePath);
            DefenderStateFile.Write(
                root, DefenderDecision.Applied, exePath,
                [.. desired.Select(e => new DefenderEntryResult(e, DefenderEntryOutcome.Added))]);

            var (lines, announce) = Recorder();
            var elevator = new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, 0, null));
            var readLineCalled = false;
            Func<string?> readLine = () => { readLineCalled = true; return "y"; };

            DefenderExclusionSetup.MaybeOfferSetup(
                root, config, HealthyDeps(elevator, exePath), interactive: true, forcePrompt: false, skip: false, announce, readLine);

            Assert.False(readLineCalled);
            Assert.Empty(elevator.Scripts);
            Assert.Contains(lines, l => l.Contains("up to date", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MaybeOfferSetup_DeltaOnly_PromptsAndAppliesOnlyTheNewEntry_KeepsPreviouslyApplied()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var exePath = @"C:\tools\unbramble.exe";

            // Simulate a prior applied run covering only the process + root path.
            var priorEntries = new List<DefenderEntry>
            {
                new(DefenderEntryType.Process, exePath),
                new(DefenderEntryType.Path, Path.GetFullPath(root)),
            };
            DefenderStateFile.Write(
                root, DefenderDecision.Applied, exePath,
                [.. priorEntries.Select(e => new DefenderEntryResult(e, DefenderEntryOutcome.Added))]);

            // Now a new junction target appears.
            var external = Path.Combine(Path.GetTempPath(), "unbramble-defender-delta-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(external);
            try
            {
                CreateJunction(Path.Combine(root, "Assets", "NewVendor"), external);

                var (lines, announce) = Recorder();
                var elevator = new SimulatedElevator(root, entries => [.. entries.Select(e => new DefenderEntryResult(e, DefenderEntryOutcome.Added))]);

                DefenderExclusionSetup.MaybeOfferSetup(
                    root, config, HealthyDeps(elevator, exePath), interactive: true, forcePrompt: false, skip: false, announce, Answers("y"));

                Assert.Single(elevator.Scripts);
                var plannedEntries = DefenderPlanFile.TryRead(DefenderPlanFile.PathFor(root));
                Assert.NotNull(plannedEntries);
                Assert.Single(plannedEntries!); // only the new junction target, not the already-covered process/root

                var state = DefenderStateFile.TryRead(root);
                Assert.Equal("applied", state!.Decision);
                Assert.Equal(3, state.Entries.Count); // process + root + new target, merged
            }
            finally
            {
                TryDelete(external);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MaybeOfferSetup_UacCancelled_DoesNotPersistDeclineAndPrintsPlainNote()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var (lines, announce) = Recorder();
            var elevator = new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.UserCancelled, -1, null));

            DefenderExclusionSetup.MaybeOfferSetup(
                root, new UnBrambleConfig { Roots = ["Assets"] }, HealthyDeps(elevator),
                interactive: true, forcePrompt: false, skip: false, announce, Answers("y"));

            Assert.Contains(lines, l => l.Contains("dismissed", StringComparison.OrdinalIgnoreCase));
            // Not treated as a persisted decline: no state file at all, so a future run tries again.
            Assert.Null(DefenderStateFile.TryRead(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MaybeOfferSetup_DevDrive_SkipsAndRecordsOnce()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var (lines1, announce1) = Recorder();
            var deps = new DefenderExclusionSetup.Dependencies(
                FakePowerShellRunner.ReturningStatus("Normal", true), new FakeRegistryReader(false), new FakeDevDriveChecker(true),
                new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, 0, null)), @"C:\tools\unbramble.exe");

            DefenderExclusionSetup.MaybeOfferSetup(root, new UnBrambleConfig { Roots = ["Assets"] }, deps, interactive: true, forcePrompt: false, skip: false, announce1, Answers());
            Assert.Contains(lines1, l => l.Contains("Dev Drive", StringComparison.Ordinal));
            var state = DefenderStateFile.TryRead(root);
            Assert.Equal("skipped-devdrive", state!.Decision);

            // Second run: already recorded -- no re-announcement.
            var (lines2, announce2) = Recorder();
            DefenderExclusionSetup.MaybeOfferSetup(root, new UnBrambleConfig { Roots = ["Assets"] }, deps, interactive: true, forcePrompt: false, skip: false, announce2, Answers());
            Assert.Empty(lines2);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void RunRemove_NoRecordedState_AnnouncesNothingToRemove()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var (lines, announce) = Recorder();
            var elevator = new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, 0, null));

            DefenderExclusionSetup.RunRemove(root, HealthyDeps(elevator), announce);

            Assert.Empty(elevator.Scripts);
            Assert.Contains(lines, l => l.Contains("nothing to remove", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void RunRemove_RecordedEntries_ElevatesAndDeletesState()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var exePath = @"C:\tools\unbramble.exe";
            DefenderStateFile.Write(
                root, DefenderDecision.Applied, exePath,
                [new DefenderEntryResult(new DefenderEntry(DefenderEntryType.Path, Path.GetFullPath(root)), DefenderEntryOutcome.Added)]);

            var (lines, announce) = Recorder();
            var elevator = new SimulatedElevator(root, entries => [.. entries.Select(e => new DefenderEntryResult(e, DefenderEntryOutcome.Removed))]);

            DefenderExclusionSetup.RunRemove(root, HealthyDeps(elevator, exePath), announce);

            Assert.Single(elevator.Scripts);
            Assert.Contains(lines, l => l.Contains("removed", StringComparison.OrdinalIgnoreCase));
            Assert.Null(DefenderStateFile.TryRead(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- DefenderApply (elevated PowerShell script builder) ----------------------------------

    [Fact]
    public void DefenderApply_BuildApplyScript_ReadsPlanWritesResult_UsesCoreCmdlets()
    {
        // The single elevated hop is now `powershell.exe` running THIS script. The script is
        // genuinely plan-in/result-out: it must embed both file paths (as PS literals it reads/
        // writes), call the real Defender cmdlets, and read `Get-MpPreference` back to verify.
        const string planPath = @"C:\proj\.unbramble\defender-plan.json";
        const string resultPath = @"C:\proj\.unbramble\defender-result.json";
        var script = DefenderApply.BuildApplyScript(planPath, resultPath);

        Assert.Contains(planPath, script, StringComparison.Ordinal);
        Assert.Contains(resultPath, script, StringComparison.Ordinal);
        Assert.Contains("Get-Content -LiteralPath $planPath", script, StringComparison.Ordinal);
        Assert.Contains("Add-MpPreference", script, StringComparison.Ordinal);
        Assert.Contains("Get-MpPreference", script, StringComparison.Ordinal);
        // Writes the result file itself (stdout can't cross the runas boundary).
        Assert.Contains("Set-Content -LiteralPath $resultPath", script, StringComparison.Ordinal);
        Assert.Contains("\"success\":", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DefenderApply_BuildRemoveScript_UsesRemoveMpPreference_AndWritesResult()
    {
        const string planPath = @"C:\proj\.unbramble\defender-plan.json";
        const string resultPath = @"C:\proj\.unbramble\defender-result.json";
        var script = DefenderApply.BuildRemoveScript(planPath, resultPath);

        Assert.Contains("Remove-MpPreference", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-MpPreference", script, StringComparison.Ordinal);
        Assert.Contains("Set-Content -LiteralPath $resultPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DefenderApply_BuildApplyScript_EscapesSingleQuotesInPaths()
    {
        // Paths are embedded as single-quoted PS literals; an apostrophe in a path (legal on
        // Windows) must be doubled or the script won't parse.
        var script = DefenderApply.BuildApplyScript(@"C:\o'brien\plan.json", @"C:\o'brien\result.json");
        Assert.Contains("'C:\\o''brien\\plan.json'", script, StringComparison.Ordinal);
        Assert.Contains("'C:\\o''brien\\result.json'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAndRecord_ElevatedLaunchFailed_RecordsNothingAndAnnounces()
    {
        // If the single elevated `powershell.exe` launch itself fails to start (the shape of the
        // real bug -- CreateProcess denied), nothing is recorded as applied and the user is told
        // the elevated step couldn't start, so a future run re-offers.
        var root = CreateTempProjectRoot();
        try
        {
            var (lines, announce) = Recorder();
            var elevator = new FixedElevator(new ElevatedLaunchResult(ElevatedLaunchOutcome.Failed, -1, "Access is denied"));

            DefenderExclusionSetup.RunSetup(root, new UnBrambleConfig { Roots = ["Assets"] }, HealthyDeps(elevator), interactive: true, announce, Answers("y"));

            Assert.Single(elevator.Scripts);
            Assert.Contains(lines, l => l.Contains("could not start the elevated step", StringComparison.OrdinalIgnoreCase));
            // No false "applied" state -- a failed launch records nothing at all.
            var state = DefenderStateFile.TryRead(root);
            Assert.True(state is null || state.Decision != "applied");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ApplyAndRecord_ElevatedRunFails_RecordsBlockedNotApplied()
    {
        // Companion to the test above, one layer up: confirms that when the elevated child
        // reports failure (as it would have for every entry in the real bug), the project's
        // recorded decision is "blocked", never "applied" -- so a subsequent `defender setup`/
        // `init` run treats the delta as still outstanding instead of silently believing it
        // already succeeded.
        var root = CreateTempProjectRoot();
        try
        {
            var elevator = new SimulatedElevator(root, planEntries =>
                planEntries.Select(e => new DefenderEntryResult(
                    e, DefenderEntryOutcome.Error, "Access is denied")).ToList());
            var deps = HealthyDeps(elevator);
            var config = new UnBrambleConfig();

            // Recorder, NEVER Console.WriteLine: this test doesn't take ConsoleTestLock.Gate, so
            // a real console sink here writes into whatever CliRunner-swapped Console.Out is
            // installed at that moment — found live as a rare parallel-load flake where these
            // announce lines ("Windows Defender setup…", "  process: …") landed at the head of
            // an unrelated test's captured `--json` stdout and broke its JsonDocument.Parse.
            var (_, announce) = Recorder();
            DefenderExclusionSetup.RunSetup(root, config, deps, interactive: true, announce, Answers("y"));

            var state = DefenderStateFile.TryRead(root);
            Assert.NotNull(state);
            Assert.Equal("blocked", state!.Decision);
            Assert.DoesNotContain(state.Entries, e => e.Outcome is "added" or "already-covered");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- Real process construction (regression: WorkingDirectory must never be the project root) --

    [Fact]
    public void DefenderProcessLaunchBuilder_BuildPowerShellStartInfo_NeverSetsProjectRootAsWorkingDirectory()
    {
        // This builder is the NON-elevated eligibility-read launch (Get-MpComputerStatus). An unset
        // `ProcessStartInfo.WorkingDirectory` does NOT mean "no working directory" -- the new process
        // silently inherits whatever the calling process's own current directory happens to be, which
        // for a user running `unbramble` from their project root would be that root. It must always
        // pin `WorkingDirectory` to a fixed, neutral, always-accessible directory instead.
        var startInfo = DefenderProcessLaunchBuilder.BuildPowerShellStartInfo("Write-Output 1");

        Assert.False(string.IsNullOrEmpty(startInfo.WorkingDirectory));
        Assert.Equal(Environment.SystemDirectory, startInfo.WorkingDirectory);
        Assert.DoesNotContain("repos", startInfo.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefenderProcessLaunchBuilder_BuildElevatedPowerShellStartInfo_ElevatesPowershellDirectly()
    {
        // The core of the single-hop fix: the ONE elevation targets `powershell.exe` itself via the
        // `runas` verb (so the elevated PowerShell's parent is the Windows AppInfo service, not the
        // unsigned unbramble.exe). Nothing in the launch references unbramble.exe any more.
        const string scriptPath = @"C:\Users\me\AppData\Local\Temp\unbramble-defender-abc123.ps1";
        var startInfo = DefenderProcessLaunchBuilder.BuildElevatedPowerShellStartInfo(scriptPath);

        Assert.Equal("powershell.exe", startInfo.FileName);
        Assert.Equal("runas", startInfo.Verb);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(Environment.SystemDirectory, startInfo.WorkingDirectory);
        Assert.DoesNotContain("unbramble.exe", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("defender-apply", startInfo.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void DefenderProcessLaunchBuilder_BuildElevatedPowerShellStartInfo_UsesFileNotEncodedCommand()
    {
        // Regression test for the THIRD real bug in this feature: a real multi-entry plan's script,
        // base64-encoded as -EncodedCommand, produced an ~8,200 character argument string -- well
        // past ShellExecuteEx's documented ~2,000 character practical lpParameters limit, which can
        // fail with an "Access is denied"-shaped error especially combined with the runas verb. This
        // reproduced on a live elevated run regardless of working directory, parent process trust,
        // calling shell, or whether the caller was already elevated -- the encoded command length was
        // the one constant. Fixed by switching to `-File <path>`, which stays under 150 characters
        // regardless of how large a plan grows, so this class of failure can't recur.
        const string scriptPath = @"C:\Users\me\AppData\Local\Temp\unbramble-defender-abc123.ps1";
        var startInfo = DefenderProcessLaunchBuilder.BuildElevatedPowerShellStartInfo(scriptPath);

        Assert.DoesNotContain("-EncodedCommand", startInfo.Arguments, StringComparison.Ordinal);
        Assert.Contains($"-File \"{scriptPath}\"", startInfo.Arguments, StringComparison.Ordinal);
        Assert.Contains("-ExecutionPolicy Bypass", startInfo.Arguments, StringComparison.Ordinal);
        Assert.True(startInfo.Arguments.Length < 500, $"expected a short, plan-size-independent argument string, got {startInfo.Arguments.Length} chars");
    }

    // ---- Round-trip serialization --------------------------------------------------------------

    [Fact]
    public void PlanFile_RoundTrips()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var path = DefenderPlanFile.PathFor(root);
            var entries = new List<DefenderEntry> { new(DefenderEntryType.Process, "exe"), new(DefenderEntryType.Path, "path1") };
            DefenderPlanFile.Write(path, entries);

            var read = DefenderPlanFile.TryRead(path);
            Assert.Equal(entries, read);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void StateFile_RoundTrips()
    {
        var root = CreateTempProjectRoot();
        try
        {
            var entries = new List<DefenderEntryResult>
            {
                new(new DefenderEntry(DefenderEntryType.Process, "exe"), DefenderEntryOutcome.Added),
                new(new DefenderEntry(DefenderEntryType.Path, "root"), DefenderEntryOutcome.AlreadyCovered),
            };
            DefenderStateFile.Write(root, DefenderDecision.Applied, "exe", entries);

            var read = DefenderStateFile.TryRead(root);
            Assert.NotNull(read);
            Assert.Equal("applied", read!.Decision);
            Assert.Equal("exe", read.ExePath);
            Assert.Equal(2, read.Entries.Count);
            Assert.Contains(read.Entries, e => e.Type == "process" && e.Outcome == "added");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- Drift detection -----------------------------------------------------------------------

    private static IndexHistoryEntry HistoryWithRoots(params (string Prefix, string? Target, long Files, double ElapsedMs)[] roots) =>
        new(
            DateTime.UtcNow, 1, false, "cli", 0, 0, 0, 0, 0, 0, 0, 0, 0,
            roots.Sum(r => r.Files), 0,
            [.. roots.Select(r => new IndexHistoryRootEntry(r.Prefix, r.Target, r.Target is not null, 1, r.Files, r.ElapsedMs))],
            []);

    [Fact]
    public void Drift_TooFewRootsWithThroughput_NoWarning()
    {
        var entry = HistoryWithRoots(("Assets", null, 100, 50));
        Assert.Null(DefenderDriftDetector.CheckForWarning(entry, null));
    }

    [Fact]
    public void Drift_UncoveredOutlierRoot_WarnsWithItsTarget()
    {
        var entry = HistoryWithRoots(
            ("Assets", null, 1000, 100),                    // 0.1 ms/file
            ("Assets/Vendor", @"D:\external\vendor", 100, 10000)); // 100 ms/file -- way slower

        var warning = DefenderDriftDetector.CheckForWarning(entry, null);
        Assert.NotNull(warning);
        Assert.Contains(@"D:\external\vendor", warning);
        Assert.Contains("defender setup", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Drift_OutlierRootAlreadyCovered_NoWarning()
    {
        var entry = HistoryWithRoots(
            ("Assets", null, 1000, 100),
            ("Assets/Vendor", @"D:\external\vendor", 100, 10000));

        var state = new DefenderStateFileJson
        {
            Decision = "applied",
            Entries = [new DefenderStateEntryJson { Type = "path", Value = @"D:\external\vendor", Outcome = "added" }],
        };

        Assert.Null(DefenderDriftDetector.CheckForWarning(entry, state));
    }

    [Fact]
    public void Drift_NoOutlier_NoWarning()
    {
        var entry = HistoryWithRoots(
            ("Assets", null, 1000, 100),
            ("Packages", null, 1000, 110));

        Assert.Null(DefenderDriftDetector.CheckForWarning(entry, null));
    }

    // ---- Junction helper (mirrors JunctionTests' own private helper) --------------------------

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start cmd.exe");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}");
        }
    }
}
