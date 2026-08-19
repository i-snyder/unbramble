using System.Xml;
using System.Xml.Linq;

namespace UnBramble.Core.CSharp;

public enum CsAnalysisMode
{
    /// <summary>Real semantic analysis: a generated IDE .csproj supplied exact defines + metadata references.</summary>
    Semantic,

    /// <summary>Honest degradation: no generated .csproj found, so no defines/references are guessed at.</summary>
    Syntactic,
}

/// <summary>One assembly's mode decision plus whatever Mode A supplied (empty for Mode B).</summary>
/// <param name="CsprojMtimeTicks">
/// The generated csproj's <c>LastWriteTimeUtc</c> in ticks, or null if no csproj exists on disk
/// for this assembly name. This is the mode fingerprint —
/// persisted per assembly and compared on the next sweep as a cheap stat (not a parse) to
/// detect a csproj appearing, disappearing, or changing without any script file itself being
/// dirty. Recorded even when the csproj is present-but-unusable (falls back to Syntactic) so a
/// later edit that makes it usable is still detected via its mtime move.
/// </param>
/// <param name="SyntacticReason">
/// Why this unit landed in Mode B — one of <see cref="CsModeReasons"/>, null for Mode A. Purely
/// diagnostic: surfaced to query/stats output so a caller can tell "no csproj was ever
/// generated" apart from "a csproj exists but Roslyn couldn't use it", instead of just seeing
/// an anonymous "syntactic" label.
/// </param>
public sealed record CsModeDecision(CsAnalysisMode Mode, IReadOnlyList<string> Defines, IReadOnlyList<string> MetadataReferencePaths, long? CsprojMtimeTicks = null, string? SyntacticReason = null);

/// <summary>Stable string reasons for why a unit is in Mode B, persisted to the <c>assemblies</c>
/// table and shown verbatim (via <see cref="Describe"/>) in query/stats caveats.</summary>
public static class CsModeReasons
{
    public const string NoCsproj = "no-csproj";
    public const string CsprojUnusable = "csproj-unusable";
    public const string CsprojParseFailed = "csproj-parse-failed";

    public static string Describe(string? reason) => reason switch
    {
        NoCsproj => "no generated .csproj",
        CsprojUnusable => ".csproj unusable",
        CsprojParseFailed => ".csproj parse failed",
        _ => "unknown",
    };
}

/// <summary>
/// Mode selection: looks for Unity's generated IDE project `&lt;name&gt;.csproj` at the project
/// root. If present and parseable, its `&lt;DefineConstants&gt;` and
/// `&lt;Reference&gt;/&lt;HintPath&gt;` entries drive Mode A (semantic). Otherwise Mode B
/// (syntactic) — never guess at defines/references.
///
/// Deliberately does NOT cross-check the generated csproj against `ProjectVersion.txt`'s Unity
/// version: Unity's generated csproj format has no single reliably-comparable version field
/// across Unity versions, so this implementation treats "present and parses to at least one
/// define or reference" as sufficient signal for Mode A.
/// </summary>
public static class CsModeSelector
{
    public static CsModeDecision Determine(string projectRoot, string assemblyName)
    {
        var csprojPath = Path.Combine(projectRoot, assemblyName + ".csproj");
        if (!File.Exists(csprojPath))
        {
            return new CsModeDecision(CsAnalysisMode.Syntactic, [], [], null, CsModeReasons.NoCsproj);
        }

        // Captured once, up front, so every return below (including the malformed-XML
        // degradation) carries the SAME fingerprint value a skip-gate check would see for
        // this csproj right now -- the fingerprint records "this file, as of this mtime", not
        // "this file, successfully parsed."
        var csprojMtimeTicks = File.GetLastWriteTimeUtc(csprojPath).Ticks;

        try
        {
            var doc = XDocument.Load(csprojPath);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var defines = doc.Descendants(ns + "DefineConstants")
                .SelectMany(e => (e.Value ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var references = new List<string>();
            foreach (var reference in doc.Descendants(ns + "Reference"))
            {
                var hintPath = reference.Element(ns + "HintPath")?.Value;
                if (string.IsNullOrWhiteSpace(hintPath))
                {
                    continue;
                }

                var normalized = hintPath.Replace('/', Path.DirectorySeparatorChar);
                var full = Path.IsPathRooted(normalized) ? normalized : Path.Combine(projectRoot, normalized);
                if (File.Exists(full))
                {
                    references.Add(full);
                }
            }

            if (defines.Count == 0 && references.Count == 0)
            {
                // Present but unusable -- honest degradation, don't guess at semantics from an
                // empty/malformed generated project.
                return new CsModeDecision(CsAnalysisMode.Syntactic, [], [], csprojMtimeTicks, CsModeReasons.CsprojUnusable);
            }

            return new CsModeDecision(CsAnalysisMode.Semantic, defines, references, csprojMtimeTicks);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return new CsModeDecision(CsAnalysisMode.Syntactic, [], [], csprojMtimeTicks, CsModeReasons.CsprojParseFailed);
        }
    }
}
