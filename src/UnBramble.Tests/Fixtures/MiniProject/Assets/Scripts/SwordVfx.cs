// Referenced only via a declaration-site field type on Sword.cs (`public SwordVfx vfx;`) --
// Sword.cs is screened (advisoryDead via the interface dispatch guard) rather than proven dead,
// and screens seed liveness (not merely suppress output), so this file must end up live -- in
// no dead bucket at all, proven or advisory -- once Sword.cs is seeded.
public class SwordVfx
{
}
