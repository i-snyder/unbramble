namespace UnBramble.Core.Model;

/// <summary>Public projection of one files-table row (used by `stats`/`resolve` internals and tests).</summary>
public sealed record FileRecord(
    string Path,
    string? Guid,
    FileKind Kind,
    long Mtime,
    long Size,
    long? MetaMtime,
    bool IdentityOnly);
