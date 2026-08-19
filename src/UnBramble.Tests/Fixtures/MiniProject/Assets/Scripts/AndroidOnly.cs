using UnityEngine;

// Referenced only from a #if UNITY_ANDROID-guarded block in Foo.Start, which the fixture's
// (and every test's) empty define set compiles out. This file has no resolvable inbound edge
// at all -- the disabled-region name_hints capture is the only thing standing between this and
// a false "provably dead" claim.
public class AndroidOnly : MonoBehaviour
{
    public void DoSomething()
    {
    }
}
