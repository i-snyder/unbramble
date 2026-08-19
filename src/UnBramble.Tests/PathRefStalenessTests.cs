using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Section 6.7 — the path-ref design lesson as a regression test: target_path_norm is
/// computed once at parse time (from the SOURCE file's own path, which never changes here)
/// and resolution against the current files table happens only at query time. Renaming the
/// TARGET must flip resolution without Menu.uxml itself ever being reparsed.
/// </summary>
public class PathRefStalenessTests
{
    [Fact]
    public void RenamingPathRefTarget_FlipsResolution_BothDirections_NoStaleCaching()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        AssertWidgetEdgeResolved(engine, expectedResolved: true);

        var oldPath = fixture.Combine("Assets", "UI", "Widget.uxml");
        var renamedPath = fixture.Combine("Assets", "UI", "Widget2.uxml");
        File.Move(oldPath, renamedPath);
        File.Move(oldPath + ".meta", renamedPath + ".meta");

        engine.RunIndex(full: false);
        AssertWidgetEdgeResolved(engine, expectedResolved: false);

        File.Move(renamedPath, oldPath);
        File.Move(renamedPath + ".meta", oldPath + ".meta");

        engine.RunIndex(full: false);
        AssertWidgetEdgeResolved(engine, expectedResolved: true);
    }

    private static void AssertWidgetEdgeResolved(UnBrambleEngine engine, bool expectedResolved)
    {
        var resolution = engine.ResolveQueryTarget("Assets/UI/Menu.uxml");
        Assert.NotNull(resolution.Target);
        var answer = engine.Uses(resolution.Target!, transitive: false, depthCap: 1);

        var widgetEdge = Assert.Single(answer.Results, r =>
            r.Kind == "path" && r.TargetKey.EndsWith("Widget.uxml", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expectedResolved, widgetEdge.Resolved);
        if (expectedResolved)
        {
            Assert.Equal("Assets/UI/Widget.uxml", widgetEdge.TargetPath, ignoreCase: true);
        }
        else
        {
            Assert.Null(widgetEdge.TargetPath);
        }
    }
}
