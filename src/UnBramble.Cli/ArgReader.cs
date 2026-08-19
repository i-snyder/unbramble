namespace UnBramble.Cli;

/// <summary>
/// Minimal hand-rolled option reader for verb argument tails (everything after args[0]).
/// Recognizes a fixed set of boolean flags and `-p`/`--path &lt;dir&gt;`; the first bare
/// (non "-"-prefixed) token is captured as the single positional argument.
/// </summary>
internal sealed class ArgReader
{
    public string? Positional { get; private set; }

    public string? Path { get; private set; }

    private readonly HashSet<string> _flags = [];

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static ArgReader Parse(IReadOnlyList<string> args, params string[] booleanFlagNames) =>
        Parse(args, booleanFlagNames, []);

    /// <summary>
    /// Parses a verb's argument tail. <paramref name="valueOptionNames"/> are options (like
    /// -p/--path) that consume the following token as a value, e.g. "--depth" for
    /// who-uses/uses. Read back via <see cref="GetValue"/>.
    /// </summary>
    public static ArgReader Parse(IReadOnlyList<string> args, string[] booleanFlagNames, string[] valueOptionNames)
    {
        var reader = new ArgReader();
        var known = new HashSet<string>(booleanFlagNames, StringComparer.Ordinal);
        var knownValues = new HashSet<string>(valueOptionNames, StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-p" or "--path")
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgReaderException($"'{arg}' requires a value");
                }

                reader.Path = args[++i];
            }
            else if (knownValues.Contains(arg))
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgReaderException($"'{arg}' requires a value");
                }

                reader._values[arg] = args[++i];
            }
            else if (known.Contains(arg))
            {
                reader._flags.Add(arg);
            }
            else if (arg.StartsWith('-'))
            {
                throw new ArgReaderException($"unknown option '{arg}'");
            }
            else if (reader.Positional is null)
            {
                reader.Positional = arg;
            }
            else
            {
                throw new ArgReaderException($"unexpected extra argument '{arg}'");
            }
        }

        return reader;
    }

    public bool HasFlag(string name) => _flags.Contains(name);

    public string? GetValue(string name) => _values.TryGetValue(name, out var v) ? v : null;

    /// <summary>The directory to start project detection from: -p/--path, else the bare positional, else cwd.</summary>
    public string ResolveStartPath() => Path ?? Positional ?? Directory.GetCurrentDirectory();

    /// <summary>
    /// The directory to start project detection from for verbs whose positional argument is
    /// NOT a path (who-uses/uses/resolve): -p/--path, else cwd. The positional is reserved
    /// for the query target/argument.
    /// </summary>
    public string ResolveStartPathIgnoringPositional() => Path ?? Directory.GetCurrentDirectory();
}

internal sealed class ArgReaderException(string message) : Exception(message);
