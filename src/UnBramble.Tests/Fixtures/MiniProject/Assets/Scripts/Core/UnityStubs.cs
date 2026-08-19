// FIXTURE STUB: minimal UnityEngine surface so the MiniProject compiles semantically
// without real Unity DLLs. Never ship shapes like this in product code.
namespace UnityEngine
{
    public class Object { }

    public class Component : Object
    {
        // Minimal stand-ins for the real Unity API surface, just enough for the regression
        // fixtures (a generic type argument in an invocation; a by-name dispatch literal) to
        // compile semantically.
        public T AddComponent<T>() where T : Component => null!;

        public void SendMessage(string methodName) { }
    }

    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }

    // Minimal stand-in for Unity's unconditional-entry-point attribute so Bootstrap.cs's
    // [RuntimeInitializeOnLoadMethod] resolves semantically (SemanticCsExtractor.IsEntryPointCandidate
    // matches by simple name only, but a real resolvable attribute class is more faithful than
    // an unresolved one).
    public class RuntimeInitializeOnLoadMethodAttribute : System.Attribute { }
}

namespace UnityEditor
{
    // Minimal stand-in for Unity's CustomEditor attribute -- deliberately not in the curated
    // inert-attribute list (Serializable/Obsolete/SerializeField/Header/Tooltip/Range), so a
    // candidate carrying it is screened rather than proven dead.
    public class CustomEditorAttribute : System.Attribute
    {
        public CustomEditorAttribute(System.Type type) { }
    }
}
