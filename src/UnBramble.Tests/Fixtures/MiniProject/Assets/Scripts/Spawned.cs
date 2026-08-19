using UnityEngine;

// A MonoBehaviour referenced only through a generic type argument
// (`AddComponent<Spawned>()` in Foo.Start) -- attached nowhere in any asset. Exercises
// type-ref extraction for generic type arguments in invocation expressions.
public class Spawned : MonoBehaviour
{
}
