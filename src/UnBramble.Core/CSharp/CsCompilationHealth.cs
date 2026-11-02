using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnBramble.Core.CSharp;

/// <summary>Semantic mode describes the analysis attempted, not successful binding. Keep
/// compiler error counts and codes with the same snapshot as the extracted graph.</summary>
public static class CsCompilationHealth
{
    public static (int ErrorCount, string? ErrorCodes) Capture(CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return (errors.Count, errors.Count == 0 ? null : string.Join(", ", errors.Select(d => d.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }
}
