namespace UnBramble.Core.Exceptions;

/// <summary>
/// Thrown by <see cref="ProjectDetection.ForceTextGate"/> when the target project is not
/// (or cannot be proven to be) using Force Text asset serialization. UnBramble's YAML/GUID
/// parsing only works on text-serialized assets, so this is a hard, fail-fast gate on every
/// command that touches the index.
/// </summary>
public sealed class ForceTextNotEnabledException : UnBrambleException
{
    private const string Suffix =
        "UnBramble's YAML parsing requires text-serialized assets.";

    public ForceTextNotEnabledException()
        : base("project is not set to Force Text serialization (Edit > Project Settings > Editor > " +
               "Asset Serialization = Force Text). " + Suffix)
    {
    }

    private ForceTextNotEnabledException(string message) : base(message)
    {
    }

    public static ForceTextNotEnabledException CouldNotVerify(string reason) =>
        new($"could not verify Force Text serialization ({reason}). " + Suffix);
}
