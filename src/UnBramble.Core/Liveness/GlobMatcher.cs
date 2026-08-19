using System.Text.RegularExpressions;

namespace UnBramble.Core.Liveness;

/// <summary>
/// Minimal glob matcher for `unbramble.json`'s `liveness.allowlist`: `*` matches any run of
/// characters except `/`, `**` matches any run
/// of characters including `/`, `?` matches exactly one non-`/` character. Matched against
/// project-relative paths (forward slashes, case-insensitive — same convention as every other
/// path comparison in this codebase). Not a general-purpose glob library — just enough for a
/// small user-authored allowlist; runtime-constructed (not `[GeneratedRegex]`, which requires a
/// compile-time-fixed pattern) but still NativeAOT-safe since it never uses
/// `RegexOptions.Compiled`.
/// </summary>
public static class GlobMatcher
{
    public static bool IsMatch(string projectRelativePath, string pattern)
    {
        var regexPattern = TranslateToRegex(pattern);
        return Regex.IsMatch(projectRelativePath, regexPattern, RegexOptions.IgnoreCase);
    }

    public static bool MatchesAny(string projectRelativePath, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(projectRelativePath, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static string TranslateToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return sb.ToString();
    }
}
