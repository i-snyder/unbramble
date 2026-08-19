// Live via a declaration-site field-type reference from Foo.cs (`public IWeapon weapon;`, the
// same mechanism as Bar below) -- Foo.cs is live (attached via Player.prefab/Level.unity), so
// this interface becomes live by ordinary propagation, with no special-casing.
public interface IWeapon
{
    void Swing();
}
