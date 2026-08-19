using UnBramble.Core.Model;

namespace UnBramble.Core.Store;

internal sealed record FileRow(
    long Id,
    string Path,
    string? Guid,
    FileKind Kind,
    long Mtime,
    long Size,
    long? MetaMtime,
    bool IdentityOnly);
