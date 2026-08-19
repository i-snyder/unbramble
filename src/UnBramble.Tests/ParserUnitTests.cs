using UnBramble.Core.Parsing;

namespace UnBramble.Tests;

/// <summary>
/// Section 6.8: parser unit tests per matrix row, on small synthetic string fragments (not
/// the fixture) — ReferenceParser only reads from disk, so each fragment is written to a
/// throwaway temp file first.
/// </summary>
public class ParserUnitTests
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
    public void EscapedJsonGuid_Positive_Extracted()
    {
        // Real Shader Graph shape: backslash-escaped quotes around guid inside a JSON string.
        var content = "\"m_SerializedTexture\": \"{\\\"texture\\\":{\\\"fileID\\\":2800000,\\\"guid\\\":\\\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeee05\\\",\\\"type\\\":3}}\"";
        var parsed = ParseFragment(content, "Assets/Shaders/X.shadergraph");
        Assert.Contains(parsed.GuidRefs, r => r.TargetGuid == "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee05");
    }

    [Fact]
    public void DashedUuid_Negative_NeverExtracted()
    {
        var content = "\"m_GuidSerialized\": \"b1fb53e3-1ca7-4a55-9f21-0e8d7c6b5a49\"";
        var parsed = ParseFragment(content, "Assets/Shaders/X.shadergraph");
        Assert.Empty(parsed.GuidRefs);
    }

    [Fact]
    public void UnprefixedHexObjectId_Negative_NeverExtracted()
    {
        // A bare 32-hex m_ObjectId with no "guid" prefix must not be mistaken for a guid ref.
        var content = "\"m_ObjectId\": \"0123456789abcdef0123456789abcdef\"";
        var parsed = ParseFragment(content, "Assets/Shaders/X.shadergraph");
        Assert.Empty(parsed.GuidRefs);
    }

    [Fact]
    public void GuidEqualsUrlForm_Extracted()
    {
        var content = "<Style src=\"project://database/Assets/UI/Styles.uss?fileID=1&amp;guid=33333333333333333333333333333309&amp;type=3\" />";
        var parsed = ParseFragment(content, "Assets/UI/X.uxml");
        Assert.Contains(parsed.GuidRefs, r => r.TargetGuid == "33333333333333333333333333333309");
    }

    [Fact]
    public void AsmdefGuidColonForm_Extracted()
    {
        var content = "{\"references\": [\"GUID:66666666666666666666666666666612\"]}";
        var parsed = ParseFragment(content, "Assets/Scripts/X.asmdef");
        Assert.Contains(parsed.GuidRefs, r => r.TargetGuid == "66666666666666666666666666666612");
    }

    [Fact]
    public void StrippedDocumentBoundary_Tolerated_RefAttributedToStrippedDoc()
    {
        var content =
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!1 &123456 stripped\n" +
            "GameObject:\n" +
            "  m_CorrespondingSourceObject: {fileID: 1, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02, type: 3}\n";
        var parsed = ParseFragment(content, "Assets/Prefabs/X.prefab");

        var r = Assert.Single(parsed.GuidRefs);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02", r.TargetGuid);
        Assert.Equal(1, r.SourceClassId);
        Assert.Equal("123456", r.SourceFileId);
    }

    [Fact]
    public void MultipleGuidMatches_OnOneLine_AllCaptured()
    {
        var content = "\"references\": [\"GUID:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01\", \"GUID:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02\"]";
        var parsed = ParseFragment(content, "Assets/Scripts/X.asmdef");

        Assert.Equal(2, parsed.GuidRefs.Count);
        Assert.Contains(parsed.GuidRefs, r => r.TargetGuid == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01");
        Assert.Contains(parsed.GuidRefs, r => r.TargetGuid == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02");
    }

    [Fact]
    public void CrlfLineEndings_Tolerated()
    {
        var content =
            "%YAML 1.1\r\n" +
            "--- !u!114 &1\r\n" +
            "MonoBehaviour:\r\n" +
            "  m_Script: {fileID: 11500000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 3}\r\n";
        var parsed = ParseFragment(content, "Assets/X.asset");

        var r = Assert.Single(parsed.GuidRefs);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01", r.TargetGuid);
        Assert.Equal(114, r.SourceClassId);
        Assert.Equal("1", r.SourceFileId);
    }

    [Fact]
    public void NullGuid_Filtered()
    {
        var content = "  m_SceneGUID: 00000000000000000000000000000000";
        var parsed = ParseFragment(content, "Assets/X.unity");
        Assert.Empty(parsed.GuidRefs);
    }

    [Fact]
    public void SelfReference_Filtered()
    {
        var content = "  script: {fileID: 0, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 3}";
        var parsed = ParseFragment(content, "Assets/X.asset", ownGuid: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01");
        Assert.Empty(parsed.GuidRefs);
    }

    [Fact]
    public void BuiltinGuid_NotFiltered_StoredLikeAnyOtherRef()
    {
        var content = "  m_Shader: {fileID: 46, guid: 0000000000000000f000000000000000, type: 0}";
        var parsed = ParseFragment(content, "Assets/X.mat");
        Assert.Contains(parsed.GuidRefs, r => r.TargetGuid == "0000000000000000f000000000000000");
    }

    [Fact]
    public void ResourceFunction_DocumentedExclusion_NeverExtracted()
    {
        var content = "--beep: resource(\"Textures/rock\");";
        var parsed = ParseFragment(content, "Assets/X.uss");
        Assert.Empty(parsed.PathRefs);
        Assert.Empty(parsed.GuidRefs);
    }

    [Fact]
    public void UssImport_PlainQuotedForm_Extracted()
    {
        var content = "@import \"Base.uss\";";
        var parsed = ParseFragment(content, "Assets/UI/X.uss");
        var r = Assert.Single(parsed.PathRefs);
        Assert.Equal("Base.uss", r.TargetPathRaw);
        Assert.Equal("Assets/UI/Base.uss", r.TargetPathNorm);
    }

    [Fact]
    public void UssImport_UrlForm_Extracted()
    {
        var content = "@import url(\"Base.uss\");";
        var parsed = ParseFragment(content, "Assets/UI/X.uss");
        var r = Assert.Single(parsed.PathRefs);
        Assert.Equal("Base.uss", r.TargetPathRaw);
    }

    [Fact]
    public void UxmlSrc_WithGuidQueryParam_NotDoubleCountedAsPathRef()
    {
        var content = "<Style src=\"project://database/Assets/UI/Styles.uss?guid=33333333333333333333333333333309\" />";
        var parsed = ParseFragment(content, "Assets/UI/X.uxml");
        Assert.Single(parsed.GuidRefs);
        Assert.Empty(parsed.PathRefs);
    }
}
