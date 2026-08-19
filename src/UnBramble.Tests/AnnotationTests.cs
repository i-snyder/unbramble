using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Section 6.3 (UnityEvent method capture) and 6.4 (GameObject name annotation).</summary>
public class AnnotationTests
{
    [Fact]
    public void LevelUnity_ToPlayerPrefab_HasAtLeastOneJumpMethodOccurrence()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Prefabs/Player.prefab");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);

        var levelEdges = answer.Results.Where(r => r.SourcePath == "Assets/Scenes/Level.unity").ToList();
        Assert.NotEmpty(levelEdges);
        Assert.Contains(levelEdges, r => r.MethodName == "Jump");
    }

    [Fact]
    public void PlayerPrefab_InternalGuidlessEvent_ProducesNoEdgeAtAll()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Prefabs/Player.prefab");
        var answer = engine.Uses(resolution.Target!, transitive: false, depthCap: 1);

        // Player.prefab's own onLocalJump UnityEvent targets a same-file fileID with no guid
        // — no guid match, so it must never produce a ref row at all, let alone one carrying
        // "Jump". The only real forward edge from Player.prefab is its own m_Script guid.
        var single = Assert.Single(answer.Results);
        Assert.Equal("Assets/Scripts/Foo.cs", single.TargetPath);
        Assert.Null(single.MethodName);
    }

    [Fact]
    public void PlayerPrefabEdge_AnnotatesGameObjectPlayer()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);

        var playerEdge = answer.Results.Single(r => r.SourcePath == "Assets/Prefabs/Player.prefab");
        Assert.Equal("Player", playerEdge.GameObject);
    }

    [Fact]
    public void LevelUnityEdge_AnnotatesGameObjectGameManager()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);

        // Scoped to the guid-kind edge specifically (the m_Script attachment): Level.unity also
        // has a second edge here (a UnityEvent -> M:Foo.Jump link, kind="event") -- SourcePath
        // alone is no longer unique for this query/target pair.
        var levelEdge = answer.Results.Single(r => r.SourcePath == "Assets/Scenes/Level.unity" && r.Kind == "guid");
        Assert.Equal("GameManager", levelEdge.GameObject);
    }

    [Fact]
    public void RenderSettingsEdge_IncompleteGameObjectChain_AnnotationOmittedNotGuessed()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // Level.unity's RenderSettings doc (m_SkyboxMaterial -> builtin f000...) is not a
        // component attached to any GameObject — "never guess": the annotation must be
        // omitted (null), not a wrong guess at some other GameObject in the same file.
        var resolution = engine.ResolveQueryTarget("Assets/Scenes/Level.unity");
        var answer = engine.Uses(resolution.Target!, transitive: false, depthCap: 1);

        var skyboxEdge = answer.Results.Single(r => r.Line == 18);
        Assert.True(skyboxEdge.Builtin);
        Assert.Null(skyboxEdge.GameObject);
    }
}
