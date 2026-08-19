namespace UnBramble.Core.Model;

public sealed record ResolveMatch(string Path, string? Guid, FileKind Kind, bool IdentityOnly);
