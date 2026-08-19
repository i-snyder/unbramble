// Implements the live IWeapon interface but is referenced nowhere directly (no guid attachment,
// no C# reference to Sword itself) -- Roslyn resolves a call through an IWeapon-typed receiver
// to IWeapon's own doc_id, not Sword's, so this implementation can have zero direct inbound
// refs while being invoked constantly at runtime. The interface/virtual-dispatch guard screens
// it (advisoryDead, never proven) and seeds it into LiveFiles (screens seed liveness exactly
// like the allowlist) -- so Sword's own private dependency (SwordVfx) survives with it instead
// of being falsely proven dead.
public class Sword : IWeapon
{
    public SwordVfx vfx;

    public void Swing()
    {
    }
}
