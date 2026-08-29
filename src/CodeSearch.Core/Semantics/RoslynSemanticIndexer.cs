using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Core.Semantics;

public sealed record SemanticIndexBuildIdentity(
    string RepositoryId,
    string GenerationId,
    string GitTree,
    string? DirtyHash,
    string BaseCommit,
    DateTime IndexedAtUtc);

/// <summary>Builds compiler-precise C# definitions, references, and type relationships.</summary>
public sealed class RoslynSemanticIndexer
{
    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.CSharpErrorMessageFormat;

    public async Task<SemanticIndex> BuildAsync(
        Solution solution,
        string repositoryRoot,
        SemanticIndexBuildIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(identity);

        var root = Path.GetFullPath(repositoryRoot);
        var documents = new List<SemanticDocument>();
        var documentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var symbols = new Dictionary<string, SemanticSymbol>(StringComparer.Ordinal);
        var occurrences = new List<SemanticOccurrence>();
        var occurrenceKeys = new HashSet<OccurrenceKey>();
        var relationships = new HashSet<SemanticRelationship>();

        foreach (var project in solution.Projects.OrderBy(project => project.FilePath, StringComparer.Ordinal))
        {
            foreach (var document in project.Documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.SourceCodeKind != SourceCodeKind.Regular ||
                    !TryRelativeSourcePath(root, document.FilePath, out var relativePath))
                {
                    continue;
                }

                // A source file can be linked into several projects in one solution. Its
                // repository path is the semantic document identity, so index the first
                // deterministic project occurrence and do not emit duplicate documents.
                if (!documentPaths.Add(relativePath))
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken);
                var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (syntaxRoot is null || semanticModel is null)
                {
                    continue;
                }

                documents.Add(new SemanticDocument
                {
                    RelPath = relativePath,
                    Hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())),
                });

                foreach (var node in syntaxRoot.DescendantNodesAndSelf())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryDeclaration(node, semanticModel, cancellationToken) is { } declaration)
                    {
                        AddOccurrence(
                            declaration.Symbol,
                            declaration.Token.Span,
                            SemanticOccurrenceRoles.Definition,
                            relativePath,
                            text,
                            symbols,
                            occurrences,
                            occurrenceKeys,
                            relationships,
                            root,
                            declaration.NodeSpan);
                    }

                    if (node is not SimpleNameSyntax name)
                    {
                        continue;
                    }

                    var alias = semanticModel.GetAliasInfo(name, cancellationToken);
                    var info = semanticModel.GetSymbolInfo(name, cancellationToken);
                    var symbol = (ISymbol?)alias ?? info.Symbol ??
                        (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
                    if (symbol is null)
                    {
                        continue;
                    }

                    AddOccurrence(
                        symbol,
                        name.Identifier.Span,
                        ReferenceRoles(name),
                        relativePath,
                        text,
                        symbols,
                        occurrences,
                        occurrenceKeys,
                        relationships,
                        root);
                }
            }
        }

        return new SemanticIndex
        {
            RepositoryId = identity.RepositoryId,
            GenerationId = identity.GenerationId,
            GitTree = identity.GitTree,
            DirtyHash = identity.DirtyHash,
            BaseCommit = identity.BaseCommit,
            IndexedAtUtc = identity.IndexedAtUtc,
            Documents = documents,
            Symbols = symbols.Values.ToList(),
            Occurrences = occurrences,
            Relationships = relationships.ToList(),
        };
    }

    private static Declaration? TryDeclaration(
        SyntaxNode node,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var token = node switch
        {
            BaseTypeDeclarationSyntax value => value.Identifier,
            DelegateDeclarationSyntax value => value.Identifier,
            MethodDeclarationSyntax value => value.Identifier,
            ConstructorDeclarationSyntax value => value.Identifier,
            DestructorDeclarationSyntax value => value.Identifier,
            PropertyDeclarationSyntax value => value.Identifier,
            EventDeclarationSyntax value => value.Identifier,
            VariableDeclaratorSyntax value => value.Identifier,
            EnumMemberDeclarationSyntax value => value.Identifier,
            ParameterSyntax value => value.Identifier,
            TypeParameterSyntax value => value.Identifier,
            LocalFunctionStatementSyntax value => value.Identifier,
            UsingDirectiveSyntax { Alias: not null } value => value.Alias.Name.Identifier,
            _ => default,
        };
        if (token == default)
        {
            return null;
        }

        return model.GetDeclaredSymbol(node, cancellationToken) is { } symbol
            ? new Declaration(symbol, token, node.Span)
            : null;
    }

    private static void AddOccurrence(
        ISymbol symbol,
        TextSpan span,
        SemanticOccurrenceRoles roles,
        string relativePath,
        SourceText text,
        Dictionary<string, SemanticSymbol> symbols,
        List<SemanticOccurrence> occurrences,
        HashSet<OccurrenceKey> occurrenceKeys,
        HashSet<SemanticRelationship> relationships,
        string repositoryRoot,
        TextSpan? enclosingSpan = null)
    {
        if (!IsNavigable(symbol))
        {
            return;
        }

        var id = AddSymbol(symbol, symbols, relationships, repositoryRoot);
        var range = ToRange(text.Lines.GetLinePositionSpan(span));
        // The whole declaration node, not just the identifier. Column-0 resolution picks the
        // outermost declaration on a line by containment over these spans, and without them
        // the rule could never fire for C# — the language whose single-line signatures it was
        // written for. The SCIP importers already carry the ranges their indexers report.
        SourceRange? enclosing = enclosingSpan is { } value
            ? ToRange(text.Lines.GetLinePositionSpan(value))
            : null;
        var key = new OccurrenceKey(relativePath, range, id);
        if (!occurrenceKeys.Add(key))
        {
            var existing = occurrences.FindIndex(occurrence =>
                occurrence.DocumentPath == relativePath &&
                occurrence.Range == range &&
                occurrence.SymbolId == id);
            occurrences[existing] = occurrences[existing] with
            {
                Roles = occurrences[existing].Roles | roles,
                EnclosingRange = occurrences[existing].EnclosingRange ?? enclosing,
            };
            return;
        }

        occurrences.Add(new SemanticOccurrence
        {
            DocumentPath = relativePath,
            Range = range,
            SymbolId = id,
            Roles = roles,
            Precision = NavigationPrecision.Precise,
            EnclosingRange = enclosing,
        });
    }

    private static string AddSymbol(
        ISymbol symbol,
        Dictionary<string, SemanticSymbol> symbols,
        HashSet<SemanticRelationship> relationships,
        string repositoryRoot)
    {
        var id = CanonicalId(symbol, repositoryRoot);
        if (!symbols.ContainsKey(id))
        {
            symbols.Add(id, new SemanticSymbol
            {
                Id = id,
                DisplayName = symbol.Name,
                Kind = Kind(symbol),
                Signature = symbol.ToDisplayString(SignatureFormat),
            });
        }

        foreach (var (target, kind) in RelatedSymbols(symbol))
        {
            if (!IsNavigable(target))
            {
                continue;
            }

            var targetId = AddSymbol(target, symbols, relationships, repositoryRoot);
            relationships.Add(new SemanticRelationship
            {
                SourceSymbolId = id,
                TargetSymbolId = targetId,
                Kind = kind,
            });
        }

        return id;
    }

    private static IEnumerable<(ISymbol Symbol, SemanticRelationshipKind Kind)> RelatedSymbols(
        ISymbol symbol)
    {
        ISymbol? overridden = symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            INamedTypeSymbol type => type.BaseType,
            _ => null,
        };
        if (overridden is not null)
        {
            yield return (
                overridden,
                symbol is INamedTypeSymbol
                    ? SemanticRelationshipKind.TypeDefinition
                    : SemanticRelationshipKind.Override);
        }

        if (symbol is INamedTypeSymbol namedType)
        {
            foreach (var @interface in namedType.Interfaces)
            {
                yield return (@interface, SemanticRelationshipKind.Implementation);
            }
        }

        IEnumerable<ISymbol> implemented = symbol switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol @event => @event.ExplicitInterfaceImplementations,
            _ => [],
        };
        foreach (var target in implemented)
        {
            yield return (target, SemanticRelationshipKind.Implementation);
        }

        if (symbol.ContainingType is { } containingType &&
            symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            foreach (var target in containingType.AllInterfaces
                         .SelectMany(@interface => @interface.GetMembers())
                         .Where(target => SymbolEqualityComparer.Default.Equals(
                             containingType.FindImplementationForInterfaceMember(target),
                             symbol)))
            {
                yield return (target, SemanticRelationshipKind.Implementation);
            }
        }
    }

    private static string CanonicalId(ISymbol symbol, string repositoryRoot)
    {
        var original = symbol.OriginalDefinition;
        if (original.GetDocumentationCommentId() is { } documentationId)
        {
            return "dotnet " + documentationId;
        }

        var containing = original.ContainingSymbol is null
            ? "global"
            : original.ContainingSymbol.GetDocumentationCommentId() ??
              original.ContainingSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var source = original.Locations.FirstOrDefault(location => location.IsInSource);
        var suffix = source is null
            ? original.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : TryRelativeSourcePath(repositoryRoot, source.SourceTree?.FilePath, out var relative)
                ? $"{relative}:{source.SourceSpan.Start}"
                : $"source:{source.SourceSpan.Start}";
        return $"dotnet-local {containing} {original.Kind} {original.Name} {suffix}";
    }

    private static SemanticSymbolKind Kind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => SemanticSymbolKind.Namespace,
        INamedTypeSymbol or ITypeParameterSymbol => SemanticSymbolKind.Type,
        IMethodSymbol => SemanticSymbolKind.Method,
        IPropertySymbol => SemanticSymbolKind.Property,
        IFieldSymbol => SemanticSymbolKind.Field,
        IEventSymbol => SemanticSymbolKind.Event,
        IParameterSymbol => SemanticSymbolKind.Parameter,
        ILocalSymbol or IAliasSymbol => SemanticSymbolKind.Local,
        _ => SemanticSymbolKind.Unknown,
    };

    private static bool IsNavigable(ISymbol symbol) =>
        !string.IsNullOrWhiteSpace(symbol.Name) &&
        symbol is not ITypeSymbol { TypeKind: TypeKind.Error };

    private static SemanticOccurrenceRoles ReferenceRoles(SimpleNameSyntax name)
    {
        var roles = SemanticOccurrenceRoles.Reference | SemanticOccurrenceRoles.Read;
        if (name.Parent is AssignmentExpressionSyntax assignment && assignment.Left == name)
        {
            roles = SemanticOccurrenceRoles.Reference | SemanticOccurrenceRoles.Write;
        }
        else if (name.Parent is ArgumentSyntax argument &&
                 !argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None))
        {
            roles |= SemanticOccurrenceRoles.Write;
        }

        return roles;
    }

    private static SourceRange ToRange(LinePositionSpan span) =>
        new(span.Start.Line, span.Start.Character, span.End.Line, span.End.Character);

    private static bool TryRelativeSourcePath(
        string repositoryRoot,
        string? filePath,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/');
        if (Path.IsPathRooted(relative) ||
            relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ||
            relative.Split('/').Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        relativePath = relative;
        return true;
    }

    private sealed record Declaration(ISymbol Symbol, SyntaxToken Token, TextSpan NodeSpan);
    private sealed record OccurrenceKey(string Path, SourceRange Range, string SymbolId);
}
