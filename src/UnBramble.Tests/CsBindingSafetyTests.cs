using System.Xml.Linq;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Generated project dependencies must bind before extraction, and incomplete binding must never support deletion claims.</summary>
public class CsBindingSafetyTests
{
    private const string EventDocId = "E:TestEvents.EventSource.Removed";
    private const string PublisherPath = "Assets/EventLibrary/EventSource.cs";
    private const string SubscriberPath = "Assets/Scripts/Entry.cs";
    private const string BindingIncomplete = "cs-binding-incomplete";

    [Fact]
    public void PredefinedProjectReference_BindsEventsNearbyCallAndHandlerParameter()
    {
        using var fixture = BindingFixture.Create(projectReference: true, nearbyReferences: true);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        var index = engine.RunIndex(full: false);

        Assert.Equal(0, index.Stats.Cs.SyntacticAssemblies);
        AssertBoundEvents(engine, EventDocId);
        Assert.Contains(engine.GetCsRefs("M:TestEvents.EventSource.Backfill"), r =>
            r.SourcePath == SubscriberPath && r.RefKind == "call" && r.Confidence == "semantic");
        Assert.Contains(engine.GetCsRefs("T:TestEvents.EntityId"), r =>
            r.SourcePath == SubscriberPath && r.RefKind == "type-ref" && r.Confidence == "semantic");
        Assert.Empty(engine.GetCsBindingFailures());
        Assert.DoesNotContain(BindingIncomplete, engine.GetCsRefsAnswer(EventDocId).BlindSpots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventOnlyPublisher_ReferencedFromPredefinedEntryPoint_IsLive(bool watchCache)
    {
        using var fixture = BindingFixture.Create(projectReference: true);
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);

        AssertBoundEvents(engine, EventDocId);
        AssertPublisherLive(engine);
    }

    [Fact]
    public void MissingProjectDependency_PersistsBindingCaveatAndRejectsLiveness()
    {
        using var fixture = BindingFixture.Create(projectReference: false);
        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            var index = engine.RunIndex(full: false);
            Assert.Equal(0, index.Stats.Cs.SyntacticAssemblies);
            AssertIncompleteBinding(engine);
        }

        // A later one-shot query must see the same limitation without rebuilding the compilation.
        using var reopened = UnBrambleEngine.Open(fixture.Root);
        AssertIncompleteBinding(reopened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectReferenceAddRemoveWithoutScriptEdits_RefreshesBindingsAndSafety(bool watchCache)
    {
        using var fixture = BindingFixture.Create(projectReference: false);
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);
        AssertIncompleteBinding(engine);

        fixture.WriteSubscriberProject(projectReference: true);
        engine.RunIndex(full: false);
        AssertBoundEvents(engine, EventDocId);
        Assert.Empty(engine.GetCsBindingFailures());
        AssertPublisherLive(engine);

        fixture.WriteSubscriberProject(projectReference: false);
        engine.RunIndex(full: false);
        AssertIncompleteBinding(engine);

        fixture.WriteSubscriberProject(projectReference: true);
        engine.RunIndex(full: false);
        AssertBoundEvents(engine, EventDocId);
        Assert.Empty(engine.GetCsBindingFailures());
        AssertPublisherLive(engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublisherEdit_InvalidatesPredefinedDependent_AndSubscriberRepairClearsFailure(bool watchCache)
    {
        using var fixture = BindingFixture.Create(projectReference: true);
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);
        AssertBoundEvents(engine, EventDocId);

        fixture.ReplaceInSource(PublisherPath, "Removed", "Gone");
        Update(engine, PublisherPath, watchCache);
        AssertIncompleteBinding(engine);

        fixture.ReplaceInSource(SubscriberPath, "Removed", "Gone");
        Update(engine, SubscriberPath, watchCache);
        Assert.Empty(engine.GetCsRefs(EventDocId));
        AssertBoundEvents(engine, "E:TestEvents.EventSource.Gone");
        Assert.Empty(engine.GetCsBindingFailures());
        AssertPublisherLive(engine);
    }

    [Fact]
    public void PublisherProjectDisappears_WatchCacheDropsStaleReference_AndRestoresCurrentSource()
    {
        using var fixture = BindingFixture.Create(projectReference: true);
        using var engine = OpenEngine(fixture.Root, watchCache: true);
        engine.RunIndex(full: false);
        AssertBoundEvents(engine, EventDocId);

        fixture.DeletePublisherProject();
        engine.RunIndex(full: false);
        AssertIncompleteBinding(engine);

        // The restored project must compile the source now on disk, not revive its old cached assembly.
        fixture.ReplaceInSource(PublisherPath, "Removed", "Gone");
        fixture.ReplaceInSource(SubscriberPath, "Removed", "Gone");
        fixture.WritePublisherProject();
        engine.RunIndex(full: false);
        Assert.Empty(engine.GetCsRefs(EventDocId));
        AssertBoundEvents(engine, "E:TestEvents.EventSource.Gone");
        Assert.Empty(engine.GetCsBindingFailures());
        AssertPublisherLive(engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastSourceInFailingAssemblyDisappears_RemovesPersistedBindingFailure(bool watchCache)
    {
        using var fixture = BindingFixture.Create(projectReference: false);
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);
        AssertIncompleteBinding(engine);

        fixture.DeleteAsset(SubscriberPath);
        var index = engine.RunIndex(full: false);

        Assert.Equal(1, index.Stats.Cs.TotalAssemblies);
        Assert.Empty(engine.GetCsBindingFailures());
        Assert.DoesNotContain(BindingIncomplete, engine.GetCsRefsAnswer(EventDocId).BlindSpots);
        var liveness = engine.RunDeadCandidates();
        Assert.True(liveness.Available, string.Join("; ", liveness.UnavailableReasons));
    }

    [Fact]
    public void AnotherEngineIndexesSourceChanges_WatchCacheCannotReviveOldTargetsOnLaterEdit()
    {
        using var fixture = BindingFixture.Create(projectReference: true);
        fixture.WriteCompanionSource("public static class Companion { public static int Value => 0; }");
        using var watcher = OpenEngine(fixture.Root, watchCache: true);
        watcher.RunIndex(full: false);
        AssertBoundEvents(watcher, EventDocId);

        fixture.ReplaceInSource(PublisherPath, "Removed", "Gone");
        fixture.ReplaceInSource(SubscriberPath, "Removed", "Gone");
        const string changedEvent = "E:TestEvents.EventSource.Gone";
        using (var independentIndexer = UnBrambleEngine.Open(fixture.Root))
        {
            independentIndexer.RunIndex(full: false);
            Assert.Empty(independentIndexer.GetCsRefs(EventDocId));
            AssertBoundEvents(independentIndexer, changedEvent);
        }

        // The independent index already committed the publisher/subscriber edits. Only this
        // different dependent source is now dirty, so old cached trees would otherwise survive.
        fixture.WriteCompanionSource("public static class Companion { public static int Value => 1; }");
        watcher.ApplyTargetedUpdate(["Assets/Scripts/Companion.cs"]);

        Assert.Empty(watcher.GetCsRefs(EventDocId));
        AssertBoundEvents(watcher, changedEvent);
        Assert.Empty(watcher.GetCsBindingFailures());
        AssertPublisherLive(watcher);
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

    private static void Update(UnBrambleEngine engine, string path, bool watchCache)
    {
        if (watchCache)
        {
            engine.ApplyTargetedUpdate([path]);
        }
        else
        {
            engine.RunIndex(full: false);
        }
    }

    private static void AssertBoundEvents(UnBrambleEngine engine, string docId)
    {
        var refs = engine.GetCsRefs(docId);
        Assert.Equal(2, refs.Count);
        Assert.All(refs, row =>
        {
            Assert.Equal(SubscriberPath, row.SourcePath);
            Assert.Equal("member-access", row.RefKind);
            Assert.Equal("semantic", row.Confidence);
        });
    }

    private static void AssertIncompleteBinding(UnBrambleEngine engine)
    {
        Assert.Empty(engine.GetCsRefs(EventDocId));
        var failure = Assert.Single(engine.GetCsBindingFailures());
        Assert.Equal("Assembly-CSharp", failure.AssemblyName);
        Assert.True(failure.ErrorCount > 0);
        Assert.Contains(BindingIncomplete, engine.GetCsRefsAnswer(EventDocId).BlindSpots);
        var widerAnswer = engine.WhoUsesSymbol(EventDocId, transitive: false, depthCap: 1);
        Assert.Contains(widerAnswer.Results, row => row.SourcePath == SubscriberPath && row.ConfidenceLabel == "speculative");
        Assert.True(widerAnswer.PossibleFalseNegative);
        var result = engine.RunDeadCandidates();
        Assert.False(result.Available);
        Assert.Contains(result.UnavailableReasons, reason => reason.Contains("incomplete C# binding", StringComparison.Ordinal));
        Assert.Empty(result.ProvenDead);
        Assert.Empty(result.AdvisoryDead);
    }

    private static void AssertPublisherLive(UnBrambleEngine engine)
    {
        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));
        Assert.Equal(1, result.Roots!.EntryPointFileCount);
        Assert.DoesNotContain(result.ProvenDead, row => row.Path == PublisherPath);
        Assert.DoesNotContain(result.AdvisoryDead, row => row.Path == PublisherPath);
    }

    private sealed class BindingFixture : IDisposable
    {
        private readonly TempDir _temp;
        private long _projectTimestamp;
        public string Root => _temp.Root;

        private BindingFixture(TempDir temp)
        {
            _temp = temp;
        }

        public static BindingFixture Create(bool projectReference, bool nearbyReferences = false)
        {
            var fixture = new BindingFixture(TempDir.Create());
            fixture.Write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2022.3.30f1\n");
            fixture.Write("ProjectSettings/EditorSettings.asset", "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");
            fixture.Write("Packages/manifest.json", "{\"dependencies\":{}}");
            fixture.WriteAsset("Assets/EventLibrary/EventsAssembly.asmdef", "{\"name\":\"EventsAssembly\"}", "22222222222222222222222222222222");
            var publisher = nearbyReferences
                ? "namespace TestEvents { public readonly struct EntityId {} public static class EventSource { public static event System.Action<EntityId> Removed; public static void Backfill() {} } }"
                : "namespace TestEvents { public static class EventSource { public static event System.Action Removed; } }";
            var subscriber = nearbyReferences
                ? "using TestEvents; public static class Entry { public static void Main() { EventSource.Removed += Handle; EventSource.Backfill(); EventSource.Removed -= Handle; } private static void Handle(EntityId id) {} }"
                : "using TestEvents; public static class Entry { public static void Main() { EventSource.Removed += Handle; EventSource.Removed -= Handle; } private static void Handle() {} }";
            fixture.WriteAsset(PublisherPath, publisher, "33333333333333333333333333333333");
            fixture.WriteAsset(SubscriberPath, subscriber, "44444444444444444444444444444444");
            fixture.WriteProject("EventsAssembly", projectReference: false);
            fixture.WriteSubscriberProject(projectReference);
            return fixture;
        }

        public void WriteSubscriberProject(bool projectReference) => WriteProject("Assembly-CSharp", projectReference);

        public void WritePublisherProject() => WriteProject("EventsAssembly", projectReference: false);

        public void DeletePublisherProject() => File.Delete(FullPath("EventsAssembly.csproj"));

        public void DeleteAsset(string relative)
        {
            File.Delete(FullPath(relative));
            File.Delete(FullPath(relative + ".meta"));
        }

        public void WriteCompanionSource(string content) =>
            WriteAsset("Assets/Scripts/Companion.cs", content, "55555555555555555555555555555555");

        public void ReplaceInSource(string relative, string oldValue, string newValue)
        {
            var path = FullPath(relative);
            var previous = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, File.ReadAllText(path).Replace(oldValue, newValue, StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(path, previous.AddSeconds(1));
        }

        private void WriteProject(string assemblyName, bool projectReference)
        {
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => Path.GetDirectoryName(path) == runtimeDirectory);
            var refs = new XElement(ns + "ItemGroup", paths.Select(path => new XElement(ns + "Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XElement(ns + "HintPath", path))));
            if (projectReference)
            {
                refs.Add(new XElement(ns + "ProjectReference", new XAttribute("Include", "EventsAssembly.csproj"), new XElement(ns + "Name", "EventsAssembly")));
            }

            var path = FullPath(assemblyName + ".csproj");
            new XDocument(new XElement(ns + "Project", refs)).Save(path);
            // Ensure successive regenerations invalidate the mtime-based mode fingerprint without sleeps.
            _projectTimestamp = Math.Max(DateTime.UtcNow.Ticks, _projectTimestamp + TimeSpan.TicksPerSecond);
            File.SetLastWriteTimeUtc(path, new DateTime(_projectTimestamp, DateTimeKind.Utc));
        }

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

        public void Dispose() => _temp.Dispose();
    }
}
