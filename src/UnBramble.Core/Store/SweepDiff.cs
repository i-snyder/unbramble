namespace UnBramble.Core.Store;

/// <param name="DirtyPaths">
/// Paths that are new or whose content/meta may have changed (new inserts, in-place updates,
/// and guid-change rebuilds) — these need their derived rows (refs/path_refs/gameobjects/
/// component_gameobject) reparsed. Removed and rebuilt-away paths are not included: their old
/// rows are already gone via ON DELETE CASCADE.
/// </param>
/// <param name="RemovedCsRelevant">
/// True if any path removed by this diff (vanished from disk, not a rebuild-away) was
/// `.cs`/`.asmdef`/`.asmref` — a removal never appears in
/// <paramref name="DirtyPaths"/>, so this is the only signal that a C#-relevant deletion
/// happened. <see cref="UnBramble.Core.UnBrambleEngine.RunCsAnalysis"/>'s skip gate must consult
/// this in addition to <paramref name="DirtyPaths"/>, or a deleted script/asmdef would leave
/// stale symbol/assembly rows behind forever.
/// </param>
/// <param name="AnyGuidRebuild">
/// True if any path in this diff was a guid-change REBUILD (old row deleted, new row inserted
/// under a fresh file id, path unchanged). Counted inside <paramref name="Changed"/> and listed
/// in <paramref name="DirtyPaths"/>, so nothing else distinguishes it from an in-place update —
/// but unlike an update it does NOT keep its file id, which matters to any consumer holding
/// file ids across passes (<see cref="UnBramble.Core.CSharp.CsSessionModelCache"/>'s cached
/// assembly-unit model): a rebuild must invalidate such caches even though Added/Removed are
/// both zero.
/// </param>
public sealed record SweepDiff(int Added, int Changed, int Removed, IReadOnlyList<string> Warnings, IReadOnlyList<string> DirtyPaths, bool RemovedCsRelevant = false, bool AnyGuidRebuild = false);
