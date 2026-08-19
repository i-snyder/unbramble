namespace UnBramble.Core.CSharp;

/// <summary>
/// Unity MonoBehaviour lifecycle/message method names: a method with one
/// of these names, declared on a type deriving (transitively) from UnityEngine.MonoBehaviour,
/// is marked `is_entry_point=1`. Feeds liveness roots; here it's just data. Not
/// claimed exhaustive against Unity's full ScriptReference message list, but covers the
/// well-known lifecycle/physics/rendering/input/animation messages sourced from Unity docs.
/// </summary>
public static class EntryPointMethods
{
    public static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "Awake", "OnEnable", "Start", "FixedUpdate", "Update", "LateUpdate",
        "OnDisable", "OnDestroy", "OnGUI", "Reset", "OnValidate",
        "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
        "OnBecameVisible", "OnBecameInvisible",
        "OnCollisionEnter", "OnCollisionEnter2D", "OnCollisionExit", "OnCollisionExit2D",
        "OnCollisionStay", "OnCollisionStay2D",
        "OnTriggerEnter", "OnTriggerEnter2D", "OnTriggerExit", "OnTriggerExit2D",
        "OnTriggerStay", "OnTriggerStay2D",
        "OnMouseDown", "OnMouseDrag", "OnMouseEnter", "OnMouseExit", "OnMouseOver",
        "OnMouseUp", "OnMouseUpAsButton",
        "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderObject", "OnRenderImage", "OnWillRenderObject",
        "OnDrawGizmos", "OnDrawGizmosSelected",
        "OnAnimatorIK", "OnAnimatorMove", "OnAudioFilterRead",
        "OnJointBreak", "OnJointBreak2D",
        "OnParticleCollision", "OnParticleTrigger",
        "OnTransformChildrenChanged", "OnTransformParentChanged",
        "OnCanvasGroupChanged", "OnRectTransformDimensionsChange",
        "OnConnectedToServer", "OnServerInitialized",
    };
}
