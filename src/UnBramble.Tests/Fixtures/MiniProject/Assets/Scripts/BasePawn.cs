using UnityEngine;

// Foo's base class. The matching cascade must find Poke() via the project-internal
// inherit-chain walk since Foo itself never declares it -- Level.unity's onLevelPoke event
// binds m_MethodName: Poke against m_TargetAssemblyTypeName: Foo, Game, and Foo has no Poke
// of its own.
public class BasePawn : MonoBehaviour
{
    public void Poke()
    {
    }
}
