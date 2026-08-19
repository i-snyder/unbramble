namespace UnBramble.Core.Model;

/// <summary>
/// One path's previously-recorded `.meta` mtime + guid, as currently persisted in the store.
/// Fed into <see cref="Scanning.Scanner.Scan"/> so the
/// scanner can skip re-reading a `.meta`'s content when its mtime hasn't moved since the last
/// sweep — the guid can't have changed if the file that encodes it hasn't been touched, the
/// same mtime-trust boundary the rest of the freshness design already stands on. A path absent
/// from the snapshot (new to the store) or whose current mtime differs always falls back to a
/// real read.
/// </summary>
public readonly record struct MetaSnapshot(long? MetaMtime, string? Guid);
