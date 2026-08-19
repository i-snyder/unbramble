using UnityEngine;

// Attached only in Assets/Dead/Orphan.prefab, which is itself unreachable from any root
// (nothing references it) -- so the m_Script attachment alone does not make this file live.
// Its method name "Vanished" deliberately collides with an unmatched UnityEvent binding in
// Level.unity (onLevelVanish -> m_MethodName: Vanished, m_TargetAssemblyTypeName: Foo, Game --
// a permanent broken-ref, since Foo never declares Vanished). Level.unity is live (a Build
// Settings root), so the unmatched raw method name screens this file -- demoting it from
// "would be provenDead" to advisoryDead (unityevent-name-collision) even though its own
// attachment site (Orphan.prefab) is dead. This deliberately exercises screen-over-proof
// precedence, not a coincidence.
public class DeadBehaviour : MonoBehaviour
{
    public void Vanished()
    {
    }
}
