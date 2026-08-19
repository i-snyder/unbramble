// A delegate-only file, referenced only via a declaration-site field type on Foo
// (`public ClickHandler onClick;`). Same join-failure shape as GameState.cs's enum case -- a
// delegate declaration is also a type declaration in Roslyn's model (an INamedTypeSymbol), so
// SemanticCsExtractor needs a VisitDelegateDeclaration override to produce a `symbols` row for
// it and be visible to cs_file_refs.
public delegate void ClickHandler();
