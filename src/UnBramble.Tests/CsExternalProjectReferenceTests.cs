using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Core.Model;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Identity-only package projects bind through Unity's compiled output without becoming source graph units.</summary>
public class CsExternalProjectReferenceTests
{
    private const string ExternalAssembly = "Package.Events";
    private const string EventDocId = "E:PackageEvents.EventSource.Removed";
    private const string SubscriberPath = "Assets/Scripts/Entry.cs";
    private const string PackageSourcePath = "Library/PackageCache/com.example.events@1.0.0/Runtime/EventSource.cs";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityOnlyProjectReference_BindsCompiledEventsMethodsAndTypesWithoutExtractingPackageSymbols(bool watchCache)
    {
        using var fixture = ExternalFixture.Create();
        using var engine = OpenEngine(fixture.Root, watchCache);

        var index = engine.RunIndex(full: false);

        Assert.Equal(1, index.Stats.Cs.TotalAssemblies);
        AssertHealthy(engine, EventDocId);
        Assert.Contains(engine.GetAllFiles(), file => file.Path == PackageSourcePath && file.IdentityOnly);
        using var connection = new SqliteConnection($"Data Source={engine.DbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbols s JOIN files f ON f.id = s.file_id WHERE f.identity_only = 1;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCompiledOutputAppears_RefreshesBindingsWithoutConsumerProjectEdits(bool watchCache)
    {
        using var fixture = ExternalFixture.Create(compiledOutput: false);
        var projectMtime = fixture.ConsumerProjectMtime;
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);
        AssertIncomplete(engine, EventDocId);

        fixture.EmitOutput();
        RefreshAfterExternalInputChange(fixture, engine, watchCache);

        Assert.Equal(projectMtime, fixture.ConsumerProjectMtime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledOutputReplacementAndRemoval_InvalidateBindingsWithoutConsumerProjectEdits(bool watchCache)
    {
        using var fixture = ExternalFixture.Create();
        var projectMtime = fixture.ConsumerProjectMtime;
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);
        AssertHealthy(engine, EventDocId);

        // Change only the excluded DLL. Package source, subscriber, and generated project stay fixed.
        fixture.EmitOutput("Gone");
        engine.RunIndex(full: false);
        AssertIncomplete(engine, EventDocId);

        fixture.ReplaceSubscriberEvent("Removed", "Gone");
        UpdateSubscriber(engine, watchCache);
        const string changedEvent = "E:PackageEvents.EventSource.Gone";
        Assert.Empty(engine.GetCsRefs(EventDocId));
        AssertHealthy(engine, changedEvent);

        fixture.DeleteOutput();
        engine.RunIndex(full: false);
        AssertIncomplete(engine, changedEvent);

        fixture.EmitOutput("Gone");
        engine.RunIndex(full: false);
        AssertHealthy(engine, changedEvent);
        Assert.Equal(projectMtime, fixture.ConsumerProjectMtime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTargetProjectAppears_RefreshesBindingsWithoutConsumerProjectEdits(bool watchCache)
    {
        using var fixture = ExternalFixture.Create(targetProject: false);
        var projectMtime = fixture.ConsumerProjectMtime;
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);
        AssertIncomplete(engine, EventDocId);

        fixture.WriteTargetProject();
        RefreshAfterExternalInputChange(fixture, engine, watchCache);

        Assert.Equal(projectMtime, fixture.ConsumerProjectMtime);
    }

    [Fact]
    public async Task FreshCompatibleWatcherHeartbeat_DoesNotHideChangedExternalOutputFromNewQueries()
    {
        using var fixture = ExternalFixture.Create();
        using var initial = UnBrambleEngine.Open(fixture.Root);
        initial.RunIndex(full: false);
        AssertHealthy(initial, EventDocId);
        using var watcher = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(watcher);
        UnBrambleEngine? reader = null;
        Task<FreshnessOutcome>? pending = null;
        var projectMtime = fixture.ConsumerProjectMtime;

        void PublishHeartbeat() => watcher.PublishFreshness(() => HeartbeatFile.Write(
            fixture.Root, Environment.ProcessId, DateTime.UtcNow, initial.StoreInstanceId, watcher.SessionId));

        try
        {
            PublishHeartbeat();
            reader = UnBrambleEngine.Open(fixture.Root);
            pending = Task.Run(() => reader.EnsureFresh());
            var unchanged = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(unchanged.SweepPerformed);
            reader.Dispose();
            reader = null;
            pending = null;

            // No watcher callback or explicit index follows either DLL replacement. A heartbeat
            // may be fresh for watched assets while this excluded compiler input has changed.
            fixture.EmitOutput("Gone");
            PublishHeartbeat();
            reader = UnBrambleEngine.Open(fixture.Root);
            pending = Task.Run(() => reader.EnsureFresh());
            var changed = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(changed.SweepPerformed);
            AssertIncomplete(reader, EventDocId);
            reader.Dispose();
            reader = null;
            pending = null;

            fixture.EmitOutput();
            PublishHeartbeat();
            reader = UnBrambleEngine.Open(fixture.Root);
            pending = Task.Run(() => reader.EnsureFresh());
            var restored = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(restored.SweepPerformed);
            AssertHealthy(reader, EventDocId);
            Assert.Equal(projectMtime, fixture.ConsumerProjectMtime);
        }
        finally
        {
            // A regression that waits for the watcher's lifetime must fail within the bound,
            // then release ownership so the worker can finish before its engine is disposed.
            watcher.Dispose();
            try
            {
                if (pending is not null)
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                reader?.Dispose();
            }
        }
    }

    private static UnBrambleEngine OpenEngine(string root, bool watchCache)
    {
        var engine = UnBrambleEngine.Open(root);
        if (watchCache)
        {
            engine.EnableWatchCompilationCache();
        }

        return engine;
    }

    private static void RefreshAfterExternalInputChange(ExternalFixture fixture, UnBrambleEngine existing, bool watchCache)
    {
        if (watchCache)
        {
            existing.RunIndex(full: false);
            AssertHealthy(existing, EventDocId);
            return;
        }

        // Exercise the persisted skip fingerprint as well as the in-process watch decision cache.
        using var reopened = UnBrambleEngine.Open(fixture.Root);
        reopened.RunIndex(full: false);
        AssertHealthy(reopened, EventDocId);
    }

    private static void UpdateSubscriber(UnBrambleEngine engine, bool watchCache)
    {
        if (watchCache)
        {
            engine.ApplyTargetedUpdate([SubscriberPath]);
        }
        else
        {
            engine.RunIndex(full: false);
        }
    }

    private static void AssertHealthy(UnBrambleEngine engine, string eventDocId)
    {
        Assert.Empty(engine.GetCsBindingFailures());
        var refs = engine.GetCsRefs(eventDocId);
        Assert.Equal(2, refs.Count);
        Assert.All(refs, row =>
        {
            Assert.Equal(SubscriberPath, row.SourcePath);
            Assert.Equal("member-access", row.RefKind);
            Assert.Equal("semantic", row.Confidence);
        });
        Assert.Contains(engine.GetCsRefs("M:PackageEvents.EventSource.Ping"), row => row.SourcePath == SubscriberPath && row.RefKind == "call" && row.Confidence == "semantic");
        Assert.Contains(engine.GetCsRefs("T:PackageEvents.EntityId"), row => row.SourcePath == SubscriberPath && row.RefKind == "type-ref" && row.Confidence == "semantic");
        Assert.DoesNotContain("cs-binding-incomplete", engine.GetCsRefsAnswer(eventDocId).BlindSpots);
    }

    private static void AssertIncomplete(UnBrambleEngine engine, string eventDocId)
    {
        Assert.Empty(engine.GetCsRefs(eventDocId));
        var failure = Assert.Single(engine.GetCsBindingFailures());
        Assert.Equal("Assembly-CSharp", failure.AssemblyName);
        Assert.True(failure.ErrorCount > 0);
        Assert.Contains("cs-binding-incomplete", engine.GetCsRefsAnswer(eventDocId).BlindSpots);
    }

    private sealed class ExternalFixture : IDisposable
    {
        private readonly TempDir _temporary;
        private long _outputTimestamp;
        public string Root => _temporary.Root;
        public DateTime ConsumerProjectMtime => File.GetLastWriteTimeUtc(FullPath("Assembly-CSharp.csproj"));

        private ExternalFixture(TempDir temporary)
        {
            _temporary = temporary;
        }

        public static ExternalFixture Create(bool compiledOutput = true, bool targetProject = true)
        {
            var fixture = new ExternalFixture(TempDir.Create());
            fixture.Write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2022.3.30f1\n");
            fixture.Write("ProjectSettings/EditorSettings.asset", "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");
            fixture.Write("Packages/manifest.json", "{\"dependencies\":{}}");
            fixture.WriteAsset("Library/PackageCache/com.example.events@1.0.0/Runtime/Package.Events.asmdef", "{\"name\":\"Package.Events\"}", "11111111111111111111111111111111");
            fixture.WriteAsset(PackageSourcePath, ExternalSource("Removed"), "22222222222222222222222222222222");
            fixture.WriteAsset(SubscriberPath, "using PackageEvents; public static class Entry { public static void Main() { EventSource.Removed += Handle; EventSource.Ping(); EventSource.Removed -= Handle; } private static void Handle(EntityId id) {} }", "33333333333333333333333333333333");
            if (targetProject)
            {
                fixture.WriteTargetProject();
            }

            fixture.WriteConsumerProject();
            if (compiledOutput)
            {
                fixture.EmitOutput();
            }

            return fixture;
        }

        public void WriteTargetProject()
        {
            // Unity's generated package projects need not supply OutputPath. The exact compiled
            // assembly is under Library/ScriptAssemblies, while its sources stay identity-only.
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
            var project = new XElement(ns + "Project",
                new XElement(ns + "PropertyGroup", new XElement(ns + "AssemblyName", ExternalAssembly)),
                new XElement(ns + "ItemGroup", new XElement(ns + "Compile", new XAttribute("Include", PackageSourcePath))));
            new XDocument(project).Save(FullPath(ExternalAssembly + ".csproj"));
        }

        public void EmitOutput(string eventName = "Removed")
        {
            var output = FullPath("Library/ScriptAssemblies/" + ExternalAssembly + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var temporaryOutput = output + ".tmp-" + Guid.NewGuid().ToString("N");
            var compilation = CSharpCompilation.Create(ExternalAssembly,
                [CSharpSyntaxTree.ParseText(ExternalSource(eventName))],
                BclPaths().Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var emitted = compilation.Emit(temporaryOutput);
            Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
            _outputTimestamp = Math.Max(DateTime.UtcNow.Ticks, _outputTimestamp + TimeSpan.TicksPerSecond);
            File.SetLastWriteTimeUtc(temporaryOutput, new DateTime(_outputTimestamp, DateTimeKind.Utc));
            File.Move(temporaryOutput, output, overwrite: true);
        }

        public void DeleteOutput() => File.Delete(FullPath("Library/ScriptAssemblies/" + ExternalAssembly + ".dll"));

        public void ReplaceSubscriberEvent(string oldName, string newName)
        {
            var path = FullPath(SubscriberPath);
            var previous = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, File.ReadAllText(path).Replace(oldName, newName, StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(path, previous.AddSeconds(1));
        }

        private void WriteConsumerProject()
        {
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
            var references = new XElement(ns + "ItemGroup", BclPaths().Select(path =>
                new XElement(ns + "Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XElement(ns + "HintPath", path))));
            references.Add(new XElement(ns + "ProjectReference", new XAttribute("Include", ExternalAssembly + ".csproj"), new XElement(ns + "Name", ExternalAssembly)));
            new XDocument(new XElement(ns + "Project", references)).Save(FullPath("Assembly-CSharp.csproj"));
        }

        private static IReadOnlyList<string> BclPaths()
        {
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => Path.GetDirectoryName(path) == runtimeDirectory).ToArray();
        }

        private static string ExternalSource(string eventName) =>
            "namespace PackageEvents { public readonly struct EntityId {} public static class EventSource { public static event System.Action<EntityId> " + eventName + "; public static void Ping() {} } }";

        private void WriteAsset(string relative, string content, string guid)
        {
            Write(relative, content);
            Write(relative + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
        }

        private void Write(string relative, string content)
        {
            var path = FullPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose() => _temporary.Dispose();
    }
}
