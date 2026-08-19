using UnBramble.Core.Parsing;

namespace UnBramble.Tests;

/// <summary>
/// v7 property-path capture (<see cref="YamlPropertyPathTracker"/> via the YAML parse pass):
/// each guid ref carries the best-effort dotted serialized-field path of its referencing line.
/// Small synthetic fragments, same temp-file pattern as <see cref="ParserUnitTests"/>.
/// </summary>
public class PropertyPathTests
{
    private static ParsedFileRefs ParseFragment(string content, string sourceProjectPath, string? ownGuid = null)
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, content);
            return new ReferenceParser().ParseContentSource(tmp, sourceProjectPath, ownGuid);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TopLevelField_PathIsBareKey_RootClassKeyExcluded()
    {
        var content =
            "--- !u!21 &2100000\n" +
            "Material:\n" +
            "  m_Shader: {fileID: 4800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 3}\n";
        var parsed = ParseFragment(content, "Assets/Mat.mat");

        var r = Assert.Single(parsed.GuidRefs);
        Assert.Equal("m_Shader", r.PropertyPath);
    }

    [Fact]
    public void NestedField_PathIsDotted()
    {
        var content =
            "--- !u!114 &1\n" +
            "MonoBehaviour:\n" +
            "  m_Settings:\n" +
            "    m_VolumeProfile: {fileID: 11400000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 2}\n";
        var parsed = ParseFragment(content, "Assets/X.asset");

        var r = Assert.Single(parsed.GuidRefs);
        Assert.Equal("m_Settings.m_VolumeProfile", r.PropertyPath);
    }

    [Fact]
    public void SequenceItems_IndexedPerItem_UnityStyleSameIndentDash()
    {
        // Unity emits block-sequence items at the SAME indent as the owning key.
        var content =
            "--- !u!23 &2300000\n" +
            "MeshRenderer:\n" +
            "  m_Materials:\n" +
            "  - {fileID: 2100000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 2}\n" +
            "  - {fileID: 2100000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02, type: 2}\n";
        var parsed = ParseFragment(content, "Assets/Scene.unity");

        Assert.Equal(2, parsed.GuidRefs.Count);
        Assert.Equal("m_Materials[0]", parsed.GuidRefs[0].PropertyPath);
        Assert.Equal("m_Materials[1]", parsed.GuidRefs[1].PropertyPath);
    }

    [Fact]
    public void MaterialTexEnvShape_FullNestedPathThroughSequenceItemKey()
    {
        var content =
            "--- !u!21 &2100000\n" +
            "Material:\n" +
            "  m_Shader: {fileID: 4800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 3}\n" +
            "  m_SavedProperties:\n" +
            "    serializedVersion: 3\n" +
            "    m_TexEnvs:\n" +
            "    - _BaseMap:\n" +
            "        m_Texture: {fileID: 2800000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02, type: 3}\n" +
            "        m_Scale: {x: 1, y: 1}\n";
        var parsed = ParseFragment(content, "Assets/Mat.mat");

        Assert.Equal(2, parsed.GuidRefs.Count);
        Assert.Equal("m_Shader", parsed.GuidRefs[0].PropertyPath);
        Assert.Equal("m_SavedProperties.m_TexEnvs[0]._BaseMap.m_Texture", parsed.GuidRefs[1].PropertyPath);
    }

    [Fact]
    public void UnityEventTarget_PathNamesTheOwningEventField()
    {
        var content =
            "--- !u!114 &114001\n" +
            "MonoBehaviour:\n" +
            "  m_OnClick:\n" +
            "    m_PersistentCalls:\n" +
            "      m_Calls:\n" +
            "      - m_Target: {fileID: 114002, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 3}\n" +
            "        m_TargetAssemblyTypeName: Foo, Assembly-CSharp\n" +
            "        m_MethodName: Jump\n" +
            "      - m_Target: {fileID: 114003, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02, type: 3}\n" +
            "        m_MethodName: Duck\n";
        var parsed = ParseFragment(content, "Assets/UI.prefab");

        Assert.Equal(2, parsed.GuidRefs.Count);
        Assert.Equal("m_OnClick.m_PersistentCalls.m_Calls[0].m_Target", parsed.GuidRefs[0].PropertyPath);
        Assert.Equal("m_OnClick.m_PersistentCalls.m_Calls[1].m_Target", parsed.GuidRefs[1].PropertyPath);
    }

    [Fact]
    public void SiblingKeyAfterNestedBlock_PopsBackToOwnDepth()
    {
        var content =
            "--- !u!114 &1\n" +
            "MonoBehaviour:\n" +
            "  m_Nested:\n" +
            "    m_Inner: {fileID: 11400000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 2}\n" +
            "  m_Sibling: {fileID: 11400000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02, type: 2}\n";
        var parsed = ParseFragment(content, "Assets/X.asset");

        Assert.Equal(2, parsed.GuidRefs.Count);
        Assert.Equal("m_Nested.m_Inner", parsed.GuidRefs[0].PropertyPath);
        Assert.Equal("m_Sibling", parsed.GuidRefs[1].PropertyPath);
    }

    [Fact]
    public void DocumentBoundary_ResetsThePath()
    {
        var content =
            "--- !u!114 &1\n" +
            "MonoBehaviour:\n" +
            "  m_Deep:\n" +
            "    m_Deeper:\n" +
            "      m_Ref: {fileID: 11400000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 2}\n" +
            "--- !u!114 &2\n" +
            "MonoBehaviour:\n" +
            "  m_Fresh: {fileID: 11400000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02, type: 2}\n";
        var parsed = ParseFragment(content, "Assets/X.asset");

        Assert.Equal(2, parsed.GuidRefs.Count);
        Assert.Equal("m_Deep.m_Deeper.m_Ref", parsed.GuidRefs[0].PropertyPath);
        Assert.Equal("m_Fresh", parsed.GuidRefs[1].PropertyPath);
    }

    [Fact]
    public void BuildSettingsScenesShape_GuidLineUnderSequenceItem()
    {
        // ProjectSettings/EditorBuildSettings.asset: the scene guid sits on its OWN key line
        // inside a sequence item — the path should name it through the item index.
        var content =
            "--- !u!1045 &1\n" +
            "EditorBuildSettings:\n" +
            "  m_Scenes:\n" +
            "  - enabled: 1\n" +
            "    path: Assets/Scenes/Main.unity\n" +
            "    guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01\n";
        var parsed = ParseFragment(content, "ProjectSettings/EditorBuildSettings.asset");

        var r = Assert.Single(parsed.GuidRefs);
        Assert.Equal("m_Scenes[0].guid", r.PropertyPath);
    }

    [Fact]
    public void NonYamlSources_PropertyPathIsNull()
    {
        var shaderGraph = ParseFragment(
            "\"m_SerializedTexture\": \"{\\\"texture\\\":{\\\"fileID\\\":2800000,\\\"guid\\\":\\\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeee05\\\",\\\"type\\\":3}}\"",
            "Assets/Shaders/X.shadergraph");
        Assert.Null(Assert.Single(shaderGraph.GuidRefs).PropertyPath);

        var uxml = ParseFragment(
            "<Style src=\"project://database/Assets/UI/Styles.uss?fileID=1&amp;guid=33333333333333333333333333333309&amp;type=3\" />",
            "Assets/UI/X.uxml");
        Assert.Null(Assert.Single(uxml.GuidRefs).PropertyPath);
    }
}
