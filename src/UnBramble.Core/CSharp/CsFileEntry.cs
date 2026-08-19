using UnBramble.Core.Model;

namespace UnBramble.Core.CSharp;

/// <summary>
/// The minimal file-inventory projection <see cref="AssemblyUnitDiscovery"/> needs — a public
/// shape so discovery is testable directly (constructing synthetic trees) without depending on
/// the store's internal row type.
/// </summary>
public sealed record CsFileEntry(long Id, string Path, string? Guid, FileKind Kind, bool IdentityOnly);
