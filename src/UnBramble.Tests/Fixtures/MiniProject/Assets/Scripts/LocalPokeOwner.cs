using UnityEngine;

// Referenced nowhere via GUID or C# code -- only its method name appears in Player.prefab's
// guid-less "onLocalPoke" UnityEvent binding (m_Target has no adjacent guid: the common
// same-asset case).
public class LocalPokeOwner : MonoBehaviour
{
    public void LocalPoke()
    {
    }
}
