using System.Text.RegularExpressions;

namespace UnBramble.Core;

/// <summary>
/// All compiled regexes used by Core, generated via <see cref="GeneratedRegexAttribute"/> so
/// they stay fast under NativeAOT (RegexOptions.Compiled silently degrades to interpreted
/// matching under AOT — never use it).
/// </summary>
internal static partial class RegexPatterns
{
    // A sibling .meta file's identity line, e.g. "guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01".
    [GeneratedRegex(@"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Multiline)]
    public static partial Regex MetaGuid();

    // ProjectSettings/EditorSettings.asset's Force Text marker.
    [GeneratedRegex(@"^\s*m_SerializationMode:\s*2\s*$", RegexOptions.Multiline)]
    public static partial Regex ForceTextMode();

    // ProjectSettings/ProjectVersion.txt's editor version line.
    [GeneratedRegex(@"^m_EditorVersion:\s*(\S+)\s*$", RegexOptions.Multiline)]
    public static partial Regex EditorVersion();

    // A bare 32-hex-digit guid, used to decide whether a `resolve` query looks like a guid.
    [GeneratedRegex(@"^[0-9a-fA-F]{32}$")]
    public static partial Regex BareGuid();

    // Handles plain YAML (guid: abc...), plain JSON ("guid": "abc..."), escaped-JSON-in-JSON
    // Shader Graph form (\"guid\":\"abc...\"), asmdef ("GUID:abc...") and UI Toolkit guid=abc...
    // URL params in one pattern. Excludes dashed UUIDs by construction (32 contiguous hex only).
    // Run per-line, iterate ALL matches (escaped-JSON lines carry several).
    [GeneratedRegex(@"(?i)(?:guid(?:\\""|"")?:\s*(?:\\""|"")?|guid=)([0-9a-f]{32})(?!\w)")]
    public static partial Regex GuidRef();

    // Unity-YAML document boundary: "--- !u!<ClassID> &<fileID>" tolerating a trailing
    // " stripped" token (stripped prefab-instance override documents). Load-bearing: without
    // tolerating " stripped", every ref in those documents is misattributed.
    [GeneratedRegex(@"^--- !u!(\d+) &(-?\d+)( stripped)?")]
    public static partial Regex YamlDocumentBoundary();

    // A GameObject document's display name.
    [GeneratedRegex(@"^\s*m_Name:\s*(.*)$")]
    public static partial Regex YamlName();

    // A component document's owning GameObject same-file link.
    [GeneratedRegex(@"^\s*m_GameObject:\s*\{fileID:\s*(-?\d+)\}")]
    public static partial Regex YamlGameObjectLink();

    // UnityEvent persistent-call bound method name (bounded lookahead target).
    [GeneratedRegex(@"^\s*m_MethodName:\s*(.*)$")]
    public static partial Regex YamlMethodName();

    // UnityEvent persistent-call bound component's serialized type (bounded lookahead target;
    // the ONLY source of the bound type -- the m_Target guid identifies the containing asset,
    // never the script).
    [GeneratedRegex(@"^\s*m_TargetAssemblyTypeName:\s*(.*)$")]
    public static partial Regex YamlTargetAssemblyTypeName();

    // Animation-clip event function name (.anim m_Events entries) -- self-contained, no
    // lookahead needed (the value is on the same line).
    [GeneratedRegex(@"^\s*m_FunctionName:\s*(.*)$")]
    public static partial Regex YamlFunctionName();

    // A double-quoted JSON string literal's contents. Used only for the asmdef
    // "precompiledReferences" array, whose values are plain DLL file names ("VendorPlugin.dll")
    // — no escape sequences occur there in practice, so the simple non-greedy form is enough and
    // a real JSON parser would buy nothing but the loss of line numbers.
    [GeneratedRegex(@"""([^""]*)""")]
    public static partial Regex JsonStringLiteral();

    // UI Toolkit .uxml: src="..." attribute values (ui:Template, Style, etc.).
    [GeneratedRegex(@"\bsrc=""([^""]*)""")]
    public static partial Regex UxmlSrc();

    // UI Toolkit .uss/.tss: url(...) function values, quotes optional.
    [GeneratedRegex(@"url\(\s*['""]?([^'"")]+)['""]?\s*\)")]
    public static partial Regex UssUrl();

    // UI Toolkit .uss/.tss: @import "..." or @import url(...).
    [GeneratedRegex(@"@import\s+(?:""([^""]*)""|url\(\s*['""]?([^'"")]+)['""]?\s*\))")]
    public static partial Regex UssImport();

    // Addressables AssetGroup's own top-level internal identity field:
    // `m_GUID: <hex32>` as a bare MonoBehaviour-root-level field on a group .asset, sibling to
    // m_GroupName:/m_Data: — the group's Addressables-internal identity, DISTINCT from its own
    // .meta guid, and NEVER a resolvable cross-asset reference (nothing in a real project ever
    // has this as its identity, so it is permanent noise in unresolved-ref counts if captured).
    // Structurally distinguished from an `m_SerializeEntries` list item's `- m_GUID: <hex32>`
    // (a REAL cross-asset addressable-target reference that must still be captured) by the
    // absence of a leading `-` list marker — a plain per-line check, no nesting-depth tracking
    // needed from the existing line-based parser. Matches ONLY the whole-line bare-field form.
    [GeneratedRegex(@"(?i)^\s*m_GUID:\s*[0-9a-f]{32}\s*$")]
    public static partial Regex AddressablesGroupSelfGuidField();

    // Leading "major.minor" of a package version string ("1.21.21" -> 1, 21). Deliberately does
    // NOT anchor the whole string: a git/file reference ("file:../local-pkg",
    // "https://github.com/...") never starts with digits, so this simply fails to match rather
    // than needing separate "is this a real version" pre-filtering.
    [GeneratedRegex(@"^(\d+)\.(\d+)")]
    public static partial Regex LeadingMajorMinorVersion();
}
