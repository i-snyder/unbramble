using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnBramble.Core.CSharp;

/// <summary>
/// Semantic-mode extraction: walks each syntax tree with its semantic
/// model, producing types/methods/properties/fields/events, inherit/override/type-ref/
/// member-access/call refs, and Unity lifecycle entry-point marking. Every resolvable symbol
/// yields `confidence='semantic'`; an unresolved/ambiguous/error reference is still recorded
/// (never dropped) with `confidence='syntactic'` — "can't tell" is data, not silence.
///
/// Type-ref coverage includes generic type arguments (anywhere, not just declaration sites)
/// and identifiers in method-body type positions (casts, is/as, local declarations);
/// non-invocation `IMethodSymbol` occurrences (method-group / delegate-conversion references)
/// are recorded as ordinary `call`-kind refs; and this extractor additionally captures
/// attribute names + entry-point reason (`symbols.attrs`/`entry_reason`) and
/// by-name-dispatch/`#if`-disabled-region evidence into `name_hints`.
/// </summary>
public static partial class SemanticCsExtractor
{
    // Known by-name dispatch APIs: a string-literal first argument to
    // any of these is captured into name_hints as negative evidence, never as an edge (the
    // dispatch target is resolved by Unity at runtime by name, not by the compiler).
    private static readonly HashSet<string> NameDispatchMethods = new(StringComparer.Ordinal)
    {
        "SendMessage", "BroadcastMessage", "SendMessageUpwards", "Invoke", "InvokeRepeating", "StartCoroutine", "CancelInvoke",
    };

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b")]
    private static partial Regex IdentifierToken();

    /// <summary>
    /// Mirrors <see cref="CsProjectAnalyzer.BuildCompilation"/>'s parse threshold -- below this
    /// many trees, per-tree <c>Parallel.For</c> scheduling overhead isn't worth it.
    /// </summary>
    private const int ParallelExtractThreshold = 8;

    public static (IReadOnlyList<CsSymbolRow> Symbols, IReadOnlyList<CsSymbolRefRow> Refs, IReadOnlyList<CsNameHintRow> NameHints) Extract(
        CSharpCompilation compilation, IReadOnlyDictionary<SyntaxTree, long> fileIdByTree)
    {
        // compilation.GetSemanticModel(tree) + the walk are safe to run per-tree in
        // parallel -- Roslyn compilations are immutable and documented as safe for concurrent
        // readers, and each tree gets its own SemanticModel instance and its own local result
        // lists (no shared mutable state between workers). Merged back afterward in
        // `compilation.SyntaxTrees`' own index order -- NOT insertion-order-of-whichever-thread-
        // finished-first -- so output row order is deterministic regardless of scheduling. This
        // matters beyond cosmetics: UnBrambleStore.ReplaceAssemblyAnalyses' docIdToRowId resolves
        // a partial-class/method doc_id collision by "first declaration wins", keyed on this
        // exact insertion order.
        var trees = compilation.SyntaxTrees.ToList();
        var perTree = new (List<CsSymbolRow> Symbols, List<CsSymbolRefRow> Refs, List<CsNameHintRow> NameHints)?[trees.Count];

        void ExtractOne(int i)
        {
            var tree = trees[i];
            if (!fileIdByTree.TryGetValue(tree, out var fileId))
            {
                return;
            }

            var localSymbols = new List<CsSymbolRow>();
            var localRefs = new List<CsSymbolRefRow>();
            var localNameHints = new List<CsNameHintRow>();

            var model = compilation.GetSemanticModel(tree);
            var walker = new Walker(model, fileId, localSymbols, localRefs, localNameHints);
            walker.Visit(tree.GetRoot());

            CollectDisabledRegionHints(tree, fileId, localNameHints);

            perTree[i] = (localSymbols, localRefs, localNameHints);
        }

        if (trees.Count >= ParallelExtractThreshold)
        {
            Parallel.For(0, trees.Count, ExtractOne);
        }
        else
        {
            for (var i = 0; i < trees.Count; i++)
            {
                ExtractOne(i);
            }
        }

        var symbols = new List<CsSymbolRow>();
        var refs = new List<CsSymbolRefRow>();
        var nameHints = new List<CsNameHintRow>();
        foreach (var entry in perTree)
        {
            if (entry is not { } e)
            {
                continue;
            }

            symbols.AddRange(e.Symbols);
            refs.AddRange(e.Refs);
            nameHints.AddRange(e.NameHints);
        }

        return (symbols, refs, nameHints);
    }

    /// <summary>
    /// Identifiers inside a `#if`-disabled region are never parsed into real syntax nodes at all
    /// (Roslyn preserves them as a single opaque <see cref="SyntaxKind.DisabledTextTrivia"/>
    /// blob), so the normal walker overrides below can never see them. This is a separate,
    /// regex-based pass over that raw trivia text — the same "smallest correct version" trade
    /// this codebase already makes for the guid regex — capturing every identifier-shaped token
    /// as `kind='cs-disabled'` negative evidence (never an edge: a stale/wrong-define
    /// compilation could be hiding a real reference here, and the later liveness screen treats
    /// any name match as "can't prove dead").
    /// </summary>
    private static void CollectDisabledRegionHints(SyntaxTree tree, long fileId, List<CsNameHintRow> nameHints)
    {
        foreach (var trivia in tree.GetRoot().DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                continue;
            }

            var startLine = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var lines = trivia.ToFullString().Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in IdentifierToken().Matches(lines[i]))
                {
                    nameHints.Add(new CsNameHintRow(fileId, m.Value, "cs-disabled", startLine + i, null));
                }
            }
        }
    }

    private sealed class Walker(SemanticModel model, long fileId, List<CsSymbolRow> symbols, List<CsSymbolRefRow> refs, List<CsNameHintRow> nameHints) : CSharpSyntaxWalker
    {
        private string? _currentMember;

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) => HandleType(node, node.Identifier, node.BaseList, () => base.VisitClassDeclaration(node));

        public override void VisitStructDeclaration(StructDeclarationSyntax node) => HandleType(node, node.Identifier, node.BaseList, () => base.VisitStructDeclaration(node));

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => HandleType(node, node.Identifier, node.BaseList, () => base.VisitInterfaceDeclaration(node));

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => HandleType(node, node.Identifier, node.BaseList, () => base.VisitRecordDeclaration(node));

        /// <summary>
        /// A standalone enum-only or delegate-only `.cs` file would otherwise produce zero
        /// `symbols` rows. A live file's declaration-site type-ref to the enum/delegate still
        /// resolves fine (Roslyn semantic resolution doesn't care whether the target has a
        /// `symbols` row), but `cs_file_refs` joins `symbol_refs.target_doc_id` against
        /// `symbols.doc_id` to find the declaring file_id -- with no `symbols` row for
        /// `T:GameState`, that join produces no file edge, no inbound edge for the enum/delegate
        /// file, and it would be emitted as provenDead despite live code referencing it. The fix:
        /// an enum declaration is a `symbols` row of kind 'type', same as class/struct/interface/
        /// record -- sufficient for the join to succeed. Individual enum member values are NOT
        /// extracted as separate symbols: nothing needs per-member liveness granularity, and the
        /// enum's own type-level symbols row is what the join needs.
        /// </summary>
        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            if (model.GetDeclaredSymbol(node) is { } symbol)
            {
                var docId = symbol.GetDocumentationCommentId() ?? $"T:{symbol.Name}";
                var attrs = JoinAttributeNames(symbol.GetAttributes().Select(a => a.AttributeClass?.Name));
                symbols.Add(new CsSymbolRow(docId, "type", symbol.Name, fileId, LineOf(node.Identifier), IsEntryPoint: false, attrs));
            }

            base.VisitEnumDeclaration(node);
        }

        /// <summary>
        /// A delegate declaration is also a type declaration in Roslyn's model (an
        /// `INamedTypeSymbol` with an `Invoke` method), with the same missing-`symbols`-row gap
        /// as enums. Return type and parameter types are also captured as ordinary type-refs
        /// (mirroring <see cref="VisitMethodDeclaration"/>'s signature handling) since a
        /// delegate's signature is exactly that -- a method signature.
        /// </summary>
        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            if (model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol)
            {
                var docId = symbol.GetDocumentationCommentId() ?? $"T:{symbol.Name}";
                var attrs = JoinAttributeNames(symbol.GetAttributes().Select(a => a.AttributeClass?.Name));
                symbols.Add(new CsSymbolRow(docId, "type", symbol.Name, fileId, LineOf(node.Identifier), IsEntryPoint: false, attrs));

                AddTypeRef(node.ReturnType, docId);
                foreach (var parameter in node.ParameterList.Parameters)
                {
                    if (parameter.Type is not null)
                    {
                        AddTypeRef(parameter.Type, docId);
                    }
                }
            }

            base.VisitDelegateDeclaration(node);
        }

        private void HandleType(SyntaxNode node, SyntaxToken identifier, BaseListSyntax? baseList, Action visitChildren)
        {
            var symbol = model.GetDeclaredSymbol(node) as INamedTypeSymbol;
            if (symbol is null)
            {
                visitChildren();
                return;
            }

            var docId = symbol.GetDocumentationCommentId() ?? $"T:{symbol.Name}";
            var attrs = JoinAttributeNames(symbol.GetAttributes().Select(a => a.AttributeClass?.Name));
            symbols.Add(new CsSymbolRow(docId, "type", symbol.Name, fileId, LineOf(identifier), IsEntryPoint: false, attrs));

            var callbackContract = FindExternalCallbackContract(symbol);
            if (callbackContract is not null)
            {
                nameHints.Add(new CsNameHintRow(
                    fileId,
                    symbol.Name,
                    "cs-unity-callback",
                    LineOf(identifier),
                    callbackContract.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
            }

            if (baseList is not null)
            {
                foreach (var baseType in baseList.Types)
                {
                    var info = model.GetSymbolInfo(baseType.Type);
                    var (targetDocId, confidence) = ResolveTarget(info, "T", baseType.Type.ToString().Trim());
                    refs.Add(new CsSymbolRefRow(fileId, docId, targetDocId, "inherit", LineOf(baseType), confidence));
                }
            }

            visitChildren();
        }

        /// <summary>External interfaces are invoked through code this project cannot inspect;
        /// Unity callback interfaces are the load-bearing case, but treating every metadata-only
        /// interface implementation conservatively also covers package/editor callback APIs.
        /// Known Unity callback base classes are included because AssetPostprocessor-style
        /// discovery likewise has no ordinary inbound C# edge. See
        /// LivenessModels.ScreenReasons.UnityCallbackGuard's doc comment for the false-provenDead
        /// bug this guards against.</summary>
        private static INamedTypeSymbol? FindExternalCallbackContract(INamedTypeSymbol symbol)
        {
            foreach (var contract in symbol.AllInterfaces)
            {
                var ns = contract.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)
                    || ns.StartsWith("UnityEditor", StringComparison.Ordinal)
                    || !contract.Locations.Any(location => location.IsInSource))
                {
                    return contract;
                }
            }

            for (var current = symbol.BaseType; current is not null; current = current.BaseType)
            {
                var fullName = current.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                if (fullName is "UnityEditor.AssetPostprocessor"
                    or "UnityEditor.AssetModificationProcessor"
                    or "UnityEditor.Build.BuildPlayerProcessor")
                {
                    return current;
                }
            }

            return null;
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var symbol = model.GetDeclaredSymbol(node);
            if (symbol is null)
            {
                base.VisitMethodDeclaration(node);
                return;
            }

            var docId = symbol.GetDocumentationCommentId() ?? $"M:{symbol.Name}";
            var attrs = JoinAttributeNames(symbol.GetAttributes().Select(a => a.AttributeClass?.Name));
            var (isEntryPoint, entryReason) = IsEntryPointCandidate(symbol);
            symbols.Add(new CsSymbolRow(docId, "method", symbol.Name, fileId, LineOf(node.Identifier), isEntryPoint, attrs, entryReason));

            if (symbol.IsOverride && symbol.OverriddenMethod is { } overridden)
            {
                var overriddenDocId = overridden.GetDocumentationCommentId() ?? $"M:{overridden.Name}";
                refs.Add(new CsSymbolRefRow(fileId, docId, overriddenDocId, "override", LineOf(node.Identifier), "semantic"));
            }

            AddTypeRef(node.ReturnType, docId);
            foreach (var parameter in node.ParameterList.Parameters)
            {
                if (parameter.Type is not null)
                {
                    AddTypeRef(parameter.Type, docId);
                }
            }

            var previous = _currentMember;
            _currentMember = docId;
            base.VisitMethodDeclaration(node);
            _currentMember = previous;
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var symbol = model.GetDeclaredSymbol(node);
            if (symbol is null)
            {
                base.VisitConstructorDeclaration(node);
                return;
            }

            var docId = symbol.GetDocumentationCommentId() ?? $"M:{symbol.ContainingType?.Name}.#ctor";
            symbols.Add(new CsSymbolRow(docId, "method", symbol.Name, fileId, LineOf(node.Identifier), IsEntryPoint: false));

            foreach (var parameter in node.ParameterList.Parameters)
            {
                if (parameter.Type is not null)
                {
                    AddTypeRef(parameter.Type, docId);
                }
            }

            var previous = _currentMember;
            _currentMember = docId;
            base.VisitConstructorDeclaration(node);
            _currentMember = previous;
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var symbol = model.GetDeclaredSymbol(node);
            if (symbol is null)
            {
                base.VisitPropertyDeclaration(node);
                return;
            }

            var docId = symbol.GetDocumentationCommentId() ?? $"P:{symbol.Name}";
            symbols.Add(new CsSymbolRow(docId, "property", symbol.Name, fileId, LineOf(node.Identifier), IsEntryPoint: false));
            AddTypeRef(node.Type, docId);

            var previous = _currentMember;
            _currentMember = docId;
            base.VisitPropertyDeclaration(node);
            _currentMember = previous;
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            // Deliberately not calling base: each declarator gets its own symbol row and its
            // own containing-member context for its initializer (fields sharing one
            // FieldDeclarationSyntax are still distinct symbols).
            foreach (var declarator in node.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(declarator) is not IFieldSymbol symbol)
                {
                    continue;
                }

                var docId = symbol.GetDocumentationCommentId() ?? $"F:{symbol.Name}";
                symbols.Add(new CsSymbolRow(docId, "field", symbol.Name, fileId, LineOf(declarator.Identifier), IsEntryPoint: false));
                AddTypeRef(node.Declaration.Type, docId);

                // Field declarations never call base for their own type node (see the comment
                // above), so a nested generic type argument (`List<Foo> items;`) would otherwise
                // never reach VisitGenericName below. This explicit Visit engages normal walker
                // recursion into the type subtree only — AddTypeRef above is not itself a walker
                // override, so this cannot double-count the outer type.
                Visit(node.Declaration.Type);

                if (declarator.Initializer is not null)
                {
                    var previous = _currentMember;
                    _currentMember = docId;
                    Visit(declarator.Initializer.Value);
                    _currentMember = previous;
                }
            }
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            foreach (var declarator in node.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(declarator) is not IEventSymbol symbol)
                {
                    continue;
                }

                var docId = symbol.GetDocumentationCommentId() ?? $"E:{symbol.Name}";
                symbols.Add(new CsSymbolRow(docId, "event", symbol.Name, fileId, LineOf(declarator.Identifier), IsEntryPoint: false));
            }
        }

        public override void VisitEventDeclaration(EventDeclarationSyntax node)
        {
            if (model.GetDeclaredSymbol(node) is { } symbol)
            {
                var docId = symbol.GetDocumentationCommentId() ?? $"E:{symbol.Name}";
                symbols.Add(new CsSymbolRow(docId, "event", symbol.Name, fileId, LineOf(node.Identifier), IsEntryPoint: false));
            }

            base.VisitEventDeclaration(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var info = model.GetSymbolInfo(node);
            var (targetDocId, confidence) = ResolveTarget(info, "M", node.Expression.ToString().Trim());
            refs.Add(new CsSymbolRefRow(fileId, _currentMember, targetDocId, "call", LineOf(node), confidence));

            CaptureNameDispatchLiteral(node);

            base.VisitInvocationExpression(node);
        }

        /// <summary>
        /// A string-literal first argument to a known by-name dispatch API
        /// (SendMessage/Invoke/StartCoroutine/...) is negative evidence for the liveness screen,
        /// never an edge — matched on the SYNTACTIC method name only (not semantic resolution),
        /// since these APIs may not even be defined in whatever UnityEngine stub or real
        /// assembly is on hand; the string is what matters, not whether the call itself resolves.
        /// </summary>
        private void CaptureNameDispatchLiteral(InvocationExpressionSyntax node)
        {
            var methodName = node.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                GenericNameSyntax gn => gn.Identifier.Text,
                _ => null,
            };

            if (methodName is null || !NameDispatchMethods.Contains(methodName))
            {
                return;
            }

            var firstArg = node.ArgumentList.Arguments.Count > 0 ? node.ArgumentList.Arguments[0].Expression : null;
            if (firstArg is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
            {
                var value = literal.Token.ValueText;
                if (value.Length > 0)
                {
                    nameHints.Add(new CsNameHintRow(fileId, value, "cs-name-literal", LineOf(node), null));
                }
            }
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var info = model.GetSymbolInfo(node);
            var fallback = $"{node.Type.ToString().Trim()}.#ctor";
            var (targetDocId, confidence) = ResolveTarget(info, "M", fallback);
            refs.Add(new CsSymbolRefRow(fileId, _currentMember, targetDocId, "call", LineOf(node), confidence));
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var isInvocationTarget = node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node;
            if (!isInvocationTarget)
            {
                var info = model.GetSymbolInfo(node);
                if (info.Symbol is IFieldSymbol or IPropertySymbol or IEventSymbol)
                {
                    var docId = info.Symbol.GetDocumentationCommentId();
                    if (docId is not null)
                    {
                        refs.Add(new CsSymbolRefRow(fileId, _currentMember, docId, "member-access", LineOf(node), "semantic"));
                    }
                }
                else if (info.Symbol is IMethodSymbol methodSymbol)
                {
                    // A resolved method symbol used OUTSIDE invocation position is a
                    // method-group / delegate-conversion reference (`AddListener(Helper.Handle)`,
                    // `Action f = Helper.Handle`, a qualified LINQ method group) — recorded as a
                    // normal semantic call-kind ref so a file referenced only this way still has
                    // an inbound edge. The BARE (unqualified) form of this same reference shape
                    // (`using static Helper; ... Action f = Handle;`) is handled by
                    // <see cref="VisitIdentifierName"/> below: every resolved IMethodSymbol
                    // occurrence outside invocation position gets a ref, with no qualified-only
                    // exemption.
                    var docId = methodSymbol.GetDocumentationCommentId();
                    if (docId is not null)
                    {
                        refs.Add(new CsSymbolRefRow(fileId, _currentMember, docId, "call", LineOf(node), "semantic"));
                    }
                }
            }

            base.VisitMemberAccessExpression(node);
        }

        /// <summary>
        /// The qualified-form method-group capture above
        /// (<see cref="VisitMemberAccessExpression"/>) only fires for `Type.Member` / `instance.
        /// Member` forms. A BARE/unqualified method-group reference -- `using static Helper; ...
        /// Action a = Handle;`, or a LINQ callback naming a method imported via `using static` --
        /// is an <see cref="IdentifierNameSyntax"/>, never a <see cref="MemberAccessExpressionSyntax"/>,
        /// and without this override would produce no ref row at all: a file referenced only
        /// this way would have zero inbound edges and be indistinguishable from genuinely dead.
        /// This mirrors the qualified-form logic exactly (same "resolved IMethodSymbol outside
        /// invocation position" rule, same call-kind/semantic-confidence shape), guarded so it
        /// never fires for an identifier already handled by one of the other two visitors: the
        /// invocation target of a bare call (`Foo()` -- handled by <see cref="VisitInvocationExpression"/>)
        /// and the `.Name` part of a qualified member access (`Helper.Handle` -- handled above)
        /// are both excluded here to avoid double-counting the same token.
        /// </summary>
        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (!IsQualifiedMemberAccessName(node) && !IsInvocationTargetPosition(node) &&
                model.GetSymbolInfo(node).Symbol is IMethodSymbol methodSymbol)
            {
                var docId = methodSymbol.GetDocumentationCommentId();
                if (docId is not null)
                {
                    refs.Add(new CsSymbolRefRow(fileId, _currentMember, docId, "call", LineOf(node), "semantic"));
                }
            }

            base.VisitIdentifierName(node);
        }

        private static bool IsQualifiedMemberAccessName(IdentifierNameSyntax node) =>
            node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == node;

        private static bool IsInvocationTargetPosition(IdentifierNameSyntax node) =>
            node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node;

        public override void VisitTypeOfExpression(TypeOfExpressionSyntax node)
        {
            AddTypeRef(node.Type, _currentMember);
            base.VisitTypeOfExpression(node);
        }

        public override void VisitCastExpression(CastExpressionSyntax node)
        {
            // Every identifier/qualified name in a method-body type position, not just
            // declaration sites.
            AddTypeRef(node.Type, _currentMember);
            base.VisitCastExpression(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.IsExpression) || node.IsKind(SyntaxKind.AsExpression))
            {
                // `expr is Foo` / `expr as Foo` (no pattern variable) parse Right as a TypeSyntax.
                if (node.Right is TypeSyntax typeSyntax)
                {
                    AddTypeRef(typeSyntax, _currentMember);
                }
            }

            base.VisitBinaryExpression(node);
        }

        public override void VisitDeclarationPattern(DeclarationPatternSyntax node)
        {
            AddTypeRef(node.Type, _currentMember);
            base.VisitDeclarationPattern(node);
        }

        public override void VisitRecursivePattern(RecursivePatternSyntax node)
        {
            if (node.Type is not null)
            {
                AddTypeRef(node.Type, _currentMember);
            }

            base.VisitRecursivePattern(node);
        }

        public override void VisitTypePattern(TypePatternSyntax node)
        {
            AddTypeRef(node.Type, _currentMember);
            base.VisitTypePattern(node);
        }

        public override void VisitVariableDeclaration(VariableDeclarationSyntax node)
        {
            // Only reaches LOCAL declarations (locals/for/using) — field/event declarations
            // handle their own type explicitly above and never call base into this node.
            AddTypeRef(node.Type, _currentMember);
            base.VisitVariableDeclaration(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            // Covers `AddComponent<Foo>()`, `GetComponent<Foo>()`, `CreateInstance<Foo>()`,
            // `new List<Foo>()`, and every other generic type argument reached by normal walker
            // traversal.
            foreach (var typeArg in node.TypeArgumentList.Arguments)
            {
                AddTypeRef(typeArg, _currentMember);
            }

            base.VisitGenericName(node);
        }

        private void AddTypeRef(TypeSyntax typeSyntax, string? sourceDocId)
        {
            var info = model.GetSymbolInfo(typeSyntax);
            var (targetDocId, confidence) = ResolveTarget(info, "T", typeSyntax.ToString().Trim());
            refs.Add(new CsSymbolRefRow(fileId, sourceDocId, targetDocId, "type-ref", LineOf(typeSyntax), confidence));
        }

        private static (string DocId, string Confidence) ResolveTarget(SymbolInfo info, string fallbackPrefix, string fallbackText)
        {
            if (info.Symbol is { } symbol)
            {
                var docId = symbol.GetDocumentationCommentId();
                if (docId is not null)
                {
                    return (docId, "semantic");
                }
            }

            foreach (var candidate in info.CandidateSymbols)
            {
                var docId = candidate.GetDocumentationCommentId();
                if (docId is not null)
                {
                    // Overload-resolution failure / ambiguity: a "can't tell", not a "no edge" --
                    // still recorded, just downgraded to syntactic.
                    return (docId, "syntactic");
                }
            }

            return ($"{fallbackPrefix}:{fallbackText}", "syntactic");
        }

        /// <summary>
        /// Distinguishes an UNCONDITIONAL entry point (fires regardless of any asset reference:
        /// `[RuntimeInitializeOnLoadMethod]`, `[InitializeOnLoadMethod]`, `static Main`) from a
        /// CONDITIONAL one (a MonoBehaviour lifecycle message name — only live if the declaring
        /// file itself is reachable).
        /// </summary>
        private static (bool IsEntryPoint, string? EntryReason) IsEntryPointCandidate(IMethodSymbol method)
        {
            if (method.Name == "Main" && method.IsStatic)
            {
                return (true, "main");
            }

            foreach (var attribute in method.GetAttributes())
            {
                var name = attribute.AttributeClass?.Name;
                if (name is "RuntimeInitializeOnLoadMethodAttribute" or "InitializeOnLoadMethodAttribute")
                {
                    return (true, "attribute");
                }
            }

            if (EntryPointMethods.Names.Contains(method.Name) && DerivesFromMonoBehaviour(method.ContainingType))
            {
                return (true, "lifecycle");
            }

            return (false, null);
        }

        private static bool DerivesFromMonoBehaviour(INamedTypeSymbol? type)
        {
            for (var t = type?.BaseType; t is not null; t = t.BaseType)
            {
                if (t.Name == "MonoBehaviour" && t.ContainingNamespace?.ToDisplayString() == "UnityEngine")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Space-joins attribute simple names for `symbols.attrs`, stripping a trailing "Attribute" suffix so `[Serializable]` and `SerializableAttribute` normalize to the same token. Null when there are none.</summary>
        private static string? JoinAttributeNames(IEnumerable<string?> rawNames)
        {
            var names = rawNames
                .Where(n => n is { Length: > 0 })
                .Select(n => StripAttributeSuffix(n!))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return names.Count == 0 ? null : string.Join(' ', names);
        }

        private static string StripAttributeSuffix(string name) =>
            name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length
                ? name[..^"Attribute".Length]
                : name;

        private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        private static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }
}
