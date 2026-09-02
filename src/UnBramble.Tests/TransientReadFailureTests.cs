using System.Text;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class TransientReadFailureTests
{
    [Fact]
    public void LockedAssetContent_DoesNotPublishEmptyReferences_AndRecoversAfterRelease()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var assetPath = fixture.Combine("Assets", "Data", "A.asset");
        using (var locked = RewriteAndHold(assetPath, "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Name: changed\n"))
        {
            var failure = Record.Exception(() => engine.RunIndex(full: false));
            Assert.NotNull(failure);
            Assert.True(ContainsException<IOException>(failure), failure.ToString());

            Assert.Contains(WhoUsesB(engine), result => result.SourcePath == "Assets/Data/A.asset");
        }

        engine.RunIndex(full: false);
        Assert.DoesNotContain(WhoUsesB(engine), result => result.SourcePath == "Assets/Data/A.asset");
    }

    [Fact]
    public void LockedMeta_DoesNotPublishMissingIdentity_AndRecoversAfterRelease()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        const string oldGuid = "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a019";
        const string newGuid = "c0c0c0c0c0c0c0c0c0c0c0c0c0c0c029";
        var metaPath = fixture.Combine("Assets", "Data", "A.asset.meta");
        using (var locked = RewriteAndHold(metaPath, $"fileFormatVersion: 2\nguid: {newGuid}\n"))
        {
            var failure = Record.Exception(() => engine.RunIndex(full: false));
            Assert.NotNull(failure);
            Assert.True(ContainsException<IOException>(failure), failure.ToString());

            var file = Assert.Single(engine.GetAllFiles(), row => row.Path == "Assets/Data/A.asset");
            Assert.Equal(oldGuid, file.Guid);
        }

        engine.RunIndex(full: false);
        var recovered = Assert.Single(engine.GetAllFiles(), row => row.Path == "Assets/Data/A.asset");
        Assert.Equal(newGuid, recovered.Guid);
    }

    private static FileStream RewriteAndHold(string path, string content)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(0);
        stream.Write(Encoding.UTF8.GetBytes(content));
        stream.Flush(flushToDisk: true);
        return stream;
    }

    private static IReadOnlyList<UnBramble.Core.Query.EdgeResult> WhoUsesB(UnBrambleEngine engine)
    {
        var target = engine.ResolveQueryTarget("Assets/Data/B.asset").Target;
        Assert.NotNull(target);
        return engine.WhoUses(target, transitive: false, depthCap: 1).Results;
    }

    private static bool ContainsException<T>(Exception exception) where T : Exception =>
        exception is T
        || exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(ContainsException<T>)
        || exception.InnerException is { } inner && ContainsException<T>(inner);
}
