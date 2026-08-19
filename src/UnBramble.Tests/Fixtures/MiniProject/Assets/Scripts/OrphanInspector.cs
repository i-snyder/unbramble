// Referenced by nothing (no guid ref, no C# call/field-type ref). [CustomEditor] is not in the
// curated inert-attribute list (Serializable/Obsolete/SerializeField/Header/Tooltip/Range) --
// reflection-driven editor tooling (CustomEditor/CustomPropertyDrawer/etc.) discovers types by
// attribute, so any non-inert attribute screens a candidate rather than letting it be proven
// dead.
[UnityEditor.CustomEditor(typeof(Foo))]
public class OrphanInspector
{
}
