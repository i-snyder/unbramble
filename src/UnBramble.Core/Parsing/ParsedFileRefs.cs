namespace UnBramble.Core.Parsing;

/// <summary>One guid-based reference extracted from a source file (maps to a `refs` row).
/// <see cref="TargetTypeName"/> is the raw `m_TargetAssemblyTypeName` value (e.g. "Foo, Game")
/// captured alongside a guid-carrying UnityEvent persistent-call target; null for every other
/// ref form. <see cref="PropertyPath"/> is the best-effort dotted serialized-field path of the
/// referencing line (see <see cref="YamlPropertyPathTracker"/>); YAML sources only, null for
/// meta/JSON/UI-Toolkit refs.</summary>
public sealed record GuidRefRow(
    string TargetGuid,
    int Line,
    int? SourceClassId,
    string? SourceFileId,
    string? MethodName,
    string? Context,
    string? TargetTypeName = null,
    string? PropertyPath = null);

/// <summary>One UI Toolkit path-based reference (maps to a `path_refs` row). Never resolved at parse time.</summary>
public sealed record PathRefRow(
    string TargetPathRaw,
    string TargetPathNorm,
    int Line,
    string? Context);

/// <summary>A GameObject document's display name (maps to a `gameobjects` row).</summary>
public sealed record GameObjectRow(string GoFileId, string Name);

/// <summary>A component document's same-file link to its owning GameObject (maps to a `component_gameobject` row).</summary>
public sealed record ComponentGameObjectRow(string ComponentFileId, string GoFileId);

/// <summary>
/// One negative-evidence name hint sourced from asset parsing (maps to a `name_hints` row):
/// `.anim` `m_FunctionName` animation-event entries (`kind='anim-event'`, no type) or guid-less
/// UnityEvent persistent-call bindings (`kind='unityevent-local'`). Never an edge — no
/// resolvable target exists for either source, so the name is captured purely as negative
/// evidence for the later liveness screen.
/// </summary>
public sealed record NameHintRow(string Name, string Kind, int Line, string? TypeName);

/// <summary>
/// One `.asmdef` `precompiledReferences` entry (maps to a `dll_refs` row): a managed plugin
/// assembly referenced by FILE NAME, which is how Unity itself resolves these — there is no guid
/// and no path in the serialized form, so neither `refs` nor `path_refs` can hold it.
/// <see cref="TargetNameNorm"/> is the lowercased file name; the raw value is kept for display.
/// Never resolved at parse time (same rule as <see cref="PathRefRow"/>): the name is matched
/// against current file names at query time, so moving the DLL can't stale the edge.
/// </summary>
public sealed record DllRefRow(
    string TargetNameRaw,
    string TargetNameNorm,
    int Line,
    string? Context);

/// <summary>Everything extracted from one source file, ready to replace that file's derived rows.</summary>
public sealed record ParsedFileRefs(
    IReadOnlyList<GuidRefRow> GuidRefs,
    IReadOnlyList<PathRefRow> PathRefs,
    IReadOnlyList<GameObjectRow> GameObjects,
    IReadOnlyList<ComponentGameObjectRow> ComponentLinks,
    IReadOnlyList<NameHintRow> NameHints,
    IReadOnlyList<DllRefRow>? DllRefs = null)
{
    public IReadOnlyList<DllRefRow> DllRefs { get; init; } = DllRefs ?? [];

    public static readonly ParsedFileRefs Empty = new([], [], [], [], []);
}
