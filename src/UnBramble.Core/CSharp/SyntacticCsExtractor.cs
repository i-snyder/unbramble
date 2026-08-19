using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnBramble.Core.CSharp;

/// <summary>
/// Syntactic-mode extraction: pure syntax-tree traversal, no semantic
/// model, no metadata references at all — honest degradation, never a guess at semantics.
/// Declarations use syntax nesting (namespace/type) to build a best-effort qualified name;
/// inheritance and call targets are matched by identifier text only, no overload/generic
/// resolution. Every row this extractor produces is `confidence='syntactic'` — the whole
/// point of this mode existing.
///
/// Entry-point marking here checks only the type's OWN immediate
/// base-list text against the literal name "MonoBehaviour" (not a transitive by-name chain
/// through other locally-declared base classes). A fuller transitive-by-name walk is a
/// reasonable next step but adds real complexity for a heuristic that's already inherently
/// approximate; this is a deliberate simplification.
/// </summary>
public static class SyntacticCsExtractor
{
    /// <summary>
    /// Syntactic mode never populates <c>NameHints</c>: the
    /// by-name-dispatch-literal and disabled-region captures are only meaningful alongside real
    /// semantic resolution, and syntactic-mode assemblies are unconditionally disqualifying for
    /// any liveness claim regardless — always an empty list here, kept
    /// only so <see cref="CsAssemblyAnalysis"/> has one shape across both modes.
    /// </summary>
    public static (IReadOnlyList<CsSymbolRow> Symbols, IReadOnlyList<CsSymbolRefRow> Refs, IReadOnlyList<CsNameHintRow> NameHints) Extract(AssemblyUnit unit, Func<string, string> toFullPath)
    {
        var symbols = new List<CsSymbolRow>();
        var refs = new List<CsSymbolRefRow>();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        foreach (var (fileId, path) in unit.Scripts)
        {
            string text;
            try
            {
                text = File.ReadAllText(toFullPath(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, parseOptions);
            var walker = new Walker(fileId, symbols, refs);
            walker.Visit(tree.GetRoot());
        }

        return (symbols, refs, []);
    }

    private sealed class Walker(long fileId, List<CsSymbolRow> symbols, List<CsSymbolRefRow> refs) : CSharpSyntaxWalker
    {
        private readonly List<string> _namespaceStack = [];
        private readonly List<(string QualifiedName, bool ImmediateMonoBehaviour)> _typeStack = [];
        private string? _currentMember;

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            _namespaceStack.Add(node.Name.ToString());
            base.VisitNamespaceDeclaration(node);
            _namespaceStack.RemoveAt(_namespaceStack.Count - 1);
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            _namespaceStack.Add(node.Name.ToString());
            base.VisitFileScopedNamespaceDeclaration(node);
            _namespaceStack.RemoveAt(_namespaceStack.Count - 1);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) => HandleType(node.Identifier, node.BaseList, node.AttributeLists, () => base.VisitClassDeclaration(node));

        public override void VisitStructDeclaration(StructDeclarationSyntax node) => HandleType(node.Identifier, node.BaseList, node.AttributeLists, () => base.VisitStructDeclaration(node));

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => HandleType(node.Identifier, node.BaseList, node.AttributeLists, () => base.VisitInterfaceDeclaration(node));

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => HandleType(node.Identifier, node.BaseList, node.AttributeLists, () => base.VisitRecordDeclaration(node));

        /// <summary>
        /// Enum/delegate declarations get symbol rows too, even though a syntactic-mode assembly
        /// is unconditionally disqualifying for liveness: a standalone enum/delegate file should
        /// still produce a `symbols` row so `who-uses`/`cs-refs` on a syntactic-mode assembly
        /// aren't needlessly blind to it.
        /// </summary>
        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            var simpleName = node.Identifier.Text;
            var qualifiedName = ComputeQualifiedName(simpleName);
            var docId = $"T:{qualifiedName}";
            var attrs = JoinAttributeNames(node.AttributeLists);
            symbols.Add(new CsSymbolRow(docId, "type", simpleName, fileId, LineOf(node.Identifier), IsEntryPoint: false, attrs));
            base.VisitEnumDeclaration(node);
        }

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            var simpleName = node.Identifier.Text;
            var qualifiedName = ComputeQualifiedName(simpleName);
            var docId = $"T:{qualifiedName}";
            var attrs = JoinAttributeNames(node.AttributeLists);
            symbols.Add(new CsSymbolRow(docId, "type", simpleName, fileId, LineOf(node.Identifier), IsEntryPoint: false, attrs));
            base.VisitDelegateDeclaration(node);
        }

        private void HandleType(SyntaxToken identifier, BaseListSyntax? baseList, SyntaxList<AttributeListSyntax> attributeLists, Action visitChildren)
        {
            var simpleName = identifier.Text;
            var qualifiedName = ComputeQualifiedName(simpleName);
            var docId = $"T:{qualifiedName}";
            var attrs = JoinAttributeNames(attributeLists);
            symbols.Add(new CsSymbolRow(docId, "type", simpleName, fileId, LineOf(identifier), IsEntryPoint: false, attrs));

            var immediateMonoBehaviour = false;
            if (baseList is not null)
            {
                foreach (var baseType in baseList.Types)
                {
                    var baseText = baseType.Type.ToString().Trim();
                    if (LastSegment(baseText) == "MonoBehaviour")
                    {
                        immediateMonoBehaviour = true;
                    }

                    refs.Add(new CsSymbolRefRow(fileId, docId, $"T:{baseText}", "inherit", LineOf(baseType), "syntactic"));
                }
            }

            _typeStack.Add((qualifiedName, immediateMonoBehaviour));
            visitChildren();
            _typeStack.RemoveAt(_typeStack.Count - 1);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var (qualifiedType, immediateMonoBehaviour) = CurrentType();
            var name = node.Identifier.Text;
            var docId = $"M:{qualifiedType}.{name}";
            var isStatic = node.Modifiers.Any(SyntaxKind.StaticKeyword);
            var hasLifecycleAttribute = node.AttributeLists
                .SelectMany(l => l.Attributes)
                .Any(a => a.Name.ToString() is "RuntimeInitializeOnLoadMethod" or "InitializeOnLoadMethod"
                    or "RuntimeInitializeOnLoadMethodAttribute" or "InitializeOnLoadMethodAttribute");
            var isMain = name == "Main" && isStatic;
            var isLifecycle = EntryPointMethods.Names.Contains(name) && immediateMonoBehaviour;
            var isEntryPoint = isMain || hasLifecycleAttribute || isLifecycle;
            var entryReason = isMain ? "main" : hasLifecycleAttribute ? "attribute" : isLifecycle ? "lifecycle" : null;
            var attrs = JoinAttributeNames(node.AttributeLists);

            symbols.Add(new CsSymbolRow(docId, "method", name, fileId, LineOf(node.Identifier), isEntryPoint, attrs, entryReason));

            var previous = _currentMember;
            _currentMember = docId;
            base.VisitMethodDeclaration(node);
            _currentMember = previous;
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var (qualifiedType, _) = CurrentType();
            var docId = $"P:{qualifiedType}.{node.Identifier.Text}";
            symbols.Add(new CsSymbolRow(docId, "property", node.Identifier.Text, fileId, LineOf(node.Identifier), IsEntryPoint: false));

            var previous = _currentMember;
            _currentMember = docId;
            base.VisitPropertyDeclaration(node);
            _currentMember = previous;
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            var (qualifiedType, _) = CurrentType();
            foreach (var declarator in node.Declaration.Variables)
            {
                var docId = $"F:{qualifiedType}.{declarator.Identifier.Text}";
                symbols.Add(new CsSymbolRow(docId, "field", declarator.Identifier.Text, fileId, LineOf(declarator.Identifier), IsEntryPoint: false));

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
            var (qualifiedType, _) = CurrentType();
            foreach (var declarator in node.Declaration.Variables)
            {
                var docId = $"E:{qualifiedType}.{declarator.Identifier.Text}";
                symbols.Add(new CsSymbolRow(docId, "event", declarator.Identifier.Text, fileId, LineOf(declarator.Identifier), IsEntryPoint: false));
            }
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var (qualifiedType, _) = CurrentType();
            string targetDocId;
            if (node.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                targetDocId = $"M:{memberAccess.Expression.ToString().Trim()}.{memberAccess.Name.Identifier.Text}";
            }
            else
            {
                var methodText = node.Expression.ToString().Trim();
                targetDocId = qualifiedType.Length > 0 ? $"M:{qualifiedType}.{methodText}" : $"M:{methodText}";
            }

            refs.Add(new CsSymbolRefRow(fileId, _currentMember, targetDocId, "call", LineOf(node), "syntactic"));
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var targetDocId = $"M:{node.Type.ToString().Trim()}.#ctor";
            refs.Add(new CsSymbolRefRow(fileId, _currentMember, targetDocId, "call", LineOf(node), "syntactic"));
            base.VisitObjectCreationExpression(node);
        }

        private (string QualifiedName, bool ImmediateMonoBehaviour) CurrentType() =>
            _typeStack.Count > 0 ? _typeStack[^1] : (string.Join('.', _namespaceStack), false);

        private string ComputeQualifiedName(string simpleName)
        {
            var prefix = _typeStack.Count > 0 ? _typeStack[^1].QualifiedName : string.Join('.', _namespaceStack);
            return string.IsNullOrEmpty(prefix) ? simpleName : $"{prefix}.{simpleName}";
        }

        /// <summary>Space-joins attribute simple names for `symbols.attrs`. Syntax-only here (no semantic resolution), so a qualified name (`UnityEngine.RuntimeInitializeOnLoadMethod`) is reduced to its last segment first. Null when there are none.</summary>
        private static string? JoinAttributeNames(SyntaxList<AttributeListSyntax> attributeLists)
        {
            var names = attributeLists
                .SelectMany(l => l.Attributes)
                .Select(a => StripAttributeSuffix(LastSegment(a.Name.ToString())))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return names.Count == 0 ? null : string.Join(' ', names);
        }

        private static string StripAttributeSuffix(string name) =>
            name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length
                ? name[..^"Attribute".Length]
                : name;

        private static string LastSegment(string dotted)
        {
            var generic = dotted.IndexOf('<');
            var trimmed = generic >= 0 ? dotted[..generic] : dotted;
            var idx = trimmed.LastIndexOf('.');
            return idx < 0 ? trimmed : trimmed[(idx + 1)..];
        }

        private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        private static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }
}
