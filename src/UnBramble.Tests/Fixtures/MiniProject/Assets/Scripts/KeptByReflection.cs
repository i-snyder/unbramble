// Referenced nowhere -- kept alive only via the fixture's unbramble.json
// (`liveness.allowlist`). Depends on KeptDep.cs via a declaration-site field type reference
// (a .cs file can't produce a guid/path ref to a non-.cs asset, so the downstream dependency
// here is another C# class rather than a plain asset). The allowlist must seed both files as
// genuinely live, not merely suppress output -- otherwise KeptDep.cs's only path to liveness
// would be silently missing.
public class KeptByReflection
{
    public KeptDep dep;
}
