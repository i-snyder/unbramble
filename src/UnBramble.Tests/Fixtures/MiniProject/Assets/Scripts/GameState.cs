// An enum-only file, referenced only via a declaration-site field type on Foo
// (`public GameState state;`), the same mechanism Bar.cs exercises for a plain class.
// SemanticCsExtractor needs a VisitEnumDeclaration override to produce a `symbols` row for it --
// the field's type-ref to `T:GameState` resolves regardless (Roslyn doesn't care), but
// `cs_file_refs` joins symbol_refs.target_doc_id against symbols.doc_id to find the declaring
// file_id, so with no symbols row for T:GameState that join produces no file edge, leaving
// GameState.cs with zero inbound edges and wrongly proven dead despite this live reference.
public enum GameState
{
    Idle,
    Running,
}
