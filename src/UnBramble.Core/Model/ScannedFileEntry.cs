namespace UnBramble.Core.Model;

/// <summary>
/// One in-scope file or meta'd folder found by the scanner, ready to be diffed against the
/// store. Not yet a database row — the store assigns ids.
/// </summary>
/// <param name="Path">Project-relative, forward slashes, as seen through any junction.</param>
/// <param name="Guid">32 lowercase hex, or null for guid-less nodes.</param>
/// <param name="Mtime">Content file's (or folder's) LastWriteTimeUtc, in ticks.</param>
/// <param name="Size">Content file's length in bytes (0 for folders).</param>
/// <param name="MetaMtime">Sibling .meta's LastWriteTimeUtc in ticks, or null if no .meta.</param>
/// <param name="IdentityOnly">True for entries under Library/PackageCache.</param>
public sealed record ScannedFileEntry(
    string Path,
    string? Guid,
    FileKind Kind,
    long Mtime,
    long Size,
    long? MetaMtime,
    bool IdentityOnly);
