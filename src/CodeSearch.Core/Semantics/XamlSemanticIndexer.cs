using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Indexing;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Core.Semantics;

/// <summary>Adds WPF, WinUI, MAUI, and Avalonia XAML semantics to an existing C# SIDX.</summary>
public sealed class XamlSemanticIndexer
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string MauiNamespace = "http://schemas.microsoft.com/dotnet/2021/maui";
    private const string AvaloniaNamespace = "https://github.com/avaloniaui";

    public SemanticIndex Supplement(SemanticIndex csharpIndex, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(csharpIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var documents = csharpIndex.Documents.ToList();
        var symbols = csharpIndex.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        var occurrences = csharpIndex.Occurrences.ToList();
        var relationships = csharpIndex.Relationships.ToList();
        var baseTypes = relationships
            .Where(relationship => relationship.Kind == SemanticRelationshipKind.TypeDefinition)
            .GroupBy(relationship => relationship.SourceSymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.TargetSymbolId).ToArray(),
                StringComparer.Ordinal);

        var xamlDocuments = new List<(XamlSyntaxDocument Syntax, string Text)>();
        foreach (var relativePath in FileScanner.Enumerate(root)
                     .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            if (!SafeSourcePath.TryResolveFile(root, relativePath, out var fullPath, out _))
            {
                continue;
            }

            var text = File.ReadAllText(fullPath);
            var syntax = XamlSyntaxDocument.Parse(relativePath, text);
            xamlDocuments.Add((syntax, text));
        }

        var resourceDefinitions = xamlDocuments
            .SelectMany(item => item.Syntax.Elements
                .SelectMany(element => element.Attributes
                    .Where(attribute => IsXamlAttribute(element, attribute, "Key"))
                    .Select(attribute => (attribute.Value, item.Syntax.DocumentPath))))
            .GroupBy(item => item.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.DocumentPath)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        foreach (var (syntax, text) in xamlDocuments)
        {
            documents.Add(new SemanticDocument
            {
                RelPath = syntax.DocumentPath,
                Hash = SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            });
            var sourceText = SourceText.From(text);
            var xamlClass = syntax.Elements
                .SelectMany(element => element.Attributes.Select(attribute => (element, attribute)))
                .FirstOrDefault(item =>
                    item.attribute.Name.EndsWith(":Class", StringComparison.Ordinal) &&
                    item.element.Namespaces.TryGetValue(
                        SplitName(item.attribute.Name).Prefix,
                        out var uri) &&
                    string.Equals(uri, XamlNamespace, StringComparison.Ordinal))
                .attribute?.Value;
            var dialect = DetectDialect(syntax, symbols, baseTypes, xamlClass);

            foreach (var element in syntax.Elements)
            {
                var type = ResolveType(element.Name, element.Namespaces, symbols, dialect);
                AddOccurrence(
                    occurrences,
                    syntax.DocumentPath,
                    element.NameRange,
                    EnsureSymbol(symbols, type.Id, type.DisplayName, SemanticSymbolKind.Type),
                    SemanticOccurrenceRoles.Reference,
                    type.Precision);

                foreach (var attribute in element.Attributes)
                {
                    if (attribute.Name is "xmlns" ||
                        attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var (prefix, localName) = SplitName(attribute.Name);
                    if (element.Namespaces.TryGetValue(prefix, out var namespaceUri) &&
                        string.Equals(namespaceUri, XamlNamespace, StringComparison.Ordinal))
                    {
                        IndexXamlLanguageAttribute(
                            attribute,
                            localName,
                            syntax.DocumentPath,
                            sourceText,
                            xamlClass,
                            element.Namespaces,
                            dialect,
                            resourceDefinitions,
                            symbols,
                            occurrences);
                        continue;
                    }

                    var member = ResolveMember(
                        attribute.Name,
                        type,
                        element.Namespaces,
                        symbols,
                        baseTypes,
                        dialect);
                    AddOccurrence(
                        occurrences,
                        syntax.DocumentPath,
                        attribute.NameRange,
                        EnsureSymbol(
                            symbols,
                            member.Id,
                            member.DisplayName,
                            member.Kind),
                        SemanticOccurrenceRoles.Reference,
                        member.Precision);

                    IndexEventHandler(
                        attribute,
                        syntax.DocumentPath,
                        xamlClass,
                        symbols,
                        occurrences);

                    IndexMarkupExtension(
                        attribute,
                        syntax.DocumentPath,
                        sourceText,
                        xamlClass,
                        resourceDefinitions,
                        symbols,
                        occurrences);
                }
            }
        }

        return csharpIndex with
        {
            Documents = documents,
            Symbols = symbols.Values.ToList(),
            Occurrences = occurrences,
            Relationships = relationships,
        };
    }

    private static void IndexEventHandler(
        XamlAttributeSyntax attribute,
        string documentPath,
        string? xamlClass,
        IReadOnlyDictionary<string, SemanticSymbol> symbols,
        List<SemanticOccurrence> occurrences)
    {
        if (string.IsNullOrWhiteSpace(xamlClass) ||
            string.IsNullOrWhiteSpace(attribute.Value) ||
            attribute.Value.Any(character =>
                !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return;
        }

        var prefix = "dotnet M:" + xamlClass + "." + attribute.Value;
        var method = symbols.Keys.FirstOrDefault(id =>
            id.Equals(prefix, StringComparison.Ordinal) ||
            id.StartsWith(prefix + "(", StringComparison.Ordinal));
        if (method is null)
        {
            return;
        }

        AddOccurrence(
            occurrences,
            documentPath,
            attribute.ValueRange,
            method,
            SemanticOccurrenceRoles.Reference,
            NavigationPrecision.Precise);
    }

    private static void IndexXamlLanguageAttribute(
        XamlAttributeSyntax attribute,
        string localName,
        string documentPath,
        SourceText sourceText,
        string? xamlClass,
        IReadOnlyDictionary<string, string> namespaces,
        XamlDialect dialect,
        IReadOnlyDictionary<string, string[]> resourceDefinitions,
        Dictionary<string, SemanticSymbol> symbols,
        List<SemanticOccurrence> occurrences)
    {
        switch (localName)
        {
            case "Class":
            {
                var id = "dotnet T:" + attribute.Value;
                AddOccurrence(
                    occurrences,
                    documentPath,
                    attribute.ValueRange,
                    EnsureSymbol(symbols, id, LastSegment(attribute.Value), SemanticSymbolKind.Type),
                    SemanticOccurrenceRoles.Definition,
                    NavigationPrecision.Precise);
                break;
            }
            case "Name":
            {
                var id = NameId(documentPath, attribute.Value, xamlClass, symbols);
                AddOccurrence(
                    occurrences,
                    documentPath,
                    attribute.ValueRange,
                    EnsureSymbol(symbols, id, attribute.Value, SemanticSymbolKind.Resource),
                    SemanticOccurrenceRoles.Definition,
                    NavigationPrecision.Precise);
                break;
            }
            case "Key":
            {
                var id = ResourceId(documentPath, attribute.Value);
                AddOccurrence(
                    occurrences,
                    documentPath,
                    attribute.ValueRange,
                    EnsureSymbol(symbols, id, attribute.Value, SemanticSymbolKind.Resource),
                    SemanticOccurrenceRoles.Definition,
                    NavigationPrecision.Precise);
                break;
            }
            case "Reference":
            {
                var id = NameId(documentPath, attribute.Value, xamlClass, symbols);
                AddOccurrence(
                    occurrences,
                    documentPath,
                    attribute.ValueRange,
                    EnsureSymbol(symbols, id, attribute.Value, SemanticSymbolKind.Resource),
                    SemanticOccurrenceRoles.Reference,
                    NavigationPrecision.Precise);
                break;
            }
            case "DataType":
            {
                var value = attribute.Value.Trim();
                if (value.StartsWith("{x:Type ", StringComparison.Ordinal) && value.EndsWith('}'))
                {
                    value = value[8..^1].Trim();
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    var type = ResolveType(value, namespaces, symbols, dialect);
                    var offset = attribute.Value.IndexOf(value, StringComparison.Ordinal);
                    AddOccurrence(
                        occurrences,
                        documentPath,
                        Subrange(sourceText, attribute.ValueRange, offset, value.Length),
                        EnsureSymbol(symbols, type.Id, type.DisplayName, type.Kind),
                        SemanticOccurrenceRoles.Reference,
                        type.Precision);
                }

                break;
            }
        }

        IndexMarkupExtension(
            attribute,
            documentPath,
            sourceText,
            xamlClass,
            resourceDefinitions,
            symbols,
            occurrences);
    }

    private static void IndexMarkupExtension(
        XamlAttributeSyntax attribute,
        string documentPath,
        SourceText sourceText,
        string? xamlClass,
        IReadOnlyDictionary<string, string[]> resourceDefinitions,
        Dictionary<string, SemanticSymbol> symbols,
        List<SemanticOccurrence> occurrences)
    {
        var value = attribute.Value.Trim();
        string? name = null;
        string? id = null;
        NavigationPrecision precision;
        if (value.StartsWith("{x:Reference ", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            name = value[13..^1].Trim();
            id = NameId(documentPath, name, xamlClass, symbols);
            precision = NavigationPrecision.Precise;
        }
        else if ((value.StartsWith("{StaticResource ", StringComparison.Ordinal) ||
                  value.StartsWith("{DynamicResource ", StringComparison.Ordinal)) &&
                 value.EndsWith('}'))
        {
            var prefixLength = value.StartsWith("{StaticResource ", StringComparison.Ordinal)
                ? 16
                : 17;
            name = value[prefixLength..^1].Trim();
            (id, precision) = ResolveResource(
                documentPath,
                name,
                resourceDefinitions);
        }
        else if ((value.StartsWith("{Binding ", StringComparison.Ordinal) ||
                  value.StartsWith("{x:Bind ", StringComparison.Ordinal) ||
                  value.StartsWith("{CompiledBinding ", StringComparison.Ordinal)) &&
                 value.EndsWith('}'))
        {
            var prefixLength = value.StartsWith("{Binding ", StringComparison.Ordinal)
                ? 9
                : value.StartsWith("{x:Bind ", StringComparison.Ordinal)
                    ? 8
                    : 17;
            var arguments = value[prefixLength..^1].Trim();
            name = arguments.StartsWith("Path=", StringComparison.Ordinal)
                ? arguments[5..].Split(',', 2)[0].Trim()
                : arguments.Split(',', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            id = "xaml-binding " + name;
            precision = NavigationPrecision.Heuristic;
        }
        else
        {
            return;
        }

        var offset = attribute.Value.IndexOf(name, StringComparison.Ordinal);
        var range = Subrange(sourceText, attribute.ValueRange, offset, name.Length);
        AddOccurrence(
            occurrences,
            documentPath,
            range,
            EnsureSymbol(symbols, id, name, SemanticSymbolKind.Resource),
            SemanticOccurrenceRoles.Reference,
            precision);
    }

    private static ResolvedSymbol ResolveType(
        string qualifiedName,
        IReadOnlyDictionary<string, string> namespaces,
        IReadOnlyDictionary<string, SemanticSymbol> symbols,
        XamlDialect dialect)
    {
        var (prefix, name) = SplitName(qualifiedName);
        namespaces.TryGetValue(prefix, out var namespaceUri);
        var clrNamespace = ClrNamespace(namespaceUri);
        if (clrNamespace is not null)
        {
            var id = "dotnet T:" + clrNamespace + "." + name;
            return new ResolvedSymbol(
                id,
                name,
                SemanticSymbolKind.Type,
                symbols.ContainsKey(id) ? NavigationPrecision.Precise : NavigationPrecision.Inferred,
                clrNamespace + "." + name);
        }

        var presentationType = PresentationType(name, dialect);
        if (IsFrameworkNamespace(namespaceUri, dialect) &&
            presentationType is not null)
        {
            return new ResolvedSymbol(
                "dotnet T:" + presentationType,
                name,
                SemanticSymbolKind.Type,
                NavigationPrecision.Precise,
                presentationType);
        }

        var fallback = $"xaml-type {namespaceUri ?? prefix} {name}";
        return new ResolvedSymbol(
            fallback,
            name,
            SemanticSymbolKind.Type,
            NavigationPrecision.Heuristic,
            null);
    }

    private static ResolvedSymbol ResolveMember(
        string attributeName,
        ResolvedSymbol elementType,
        IReadOnlyDictionary<string, string> namespaces,
        IReadOnlyDictionary<string, SemanticSymbol> symbols,
        IReadOnlyDictionary<string, string[]> baseTypes,
        XamlDialect dialect)
    {
        var memberName = attributeName;
        var owner = elementType;
        var dot = attributeName.IndexOf('.');
        if (dot >= 0)
        {
            owner = ResolveType(attributeName[..dot], namespaces, symbols, dialect);
            memberName = attributeName[(dot + 1)..];
        }

        foreach (var typeId in TypeAndBases(owner.Id, baseTypes))
        {
            if (!typeId.StartsWith("dotnet T:", StringComparison.Ordinal))
            {
                continue;
            }

            var qualifiedType = typeId[9..];
            foreach (var (prefix, kind) in new[]
                     {
                         ("P:", SemanticSymbolKind.Property),
                         ("E:", SemanticSymbolKind.Event),
                         ("F:", SemanticSymbolKind.Field),
                     })
            {
                var id = "dotnet " + prefix + qualifiedType + "." + memberName;
                if (symbols.ContainsKey(id))
                {
                    return new ResolvedSymbol(
                        id,
                        memberName,
                        kind,
                        NavigationPrecision.Precise,
                        null);
                }
            }

            var propertyField = "dotnet F:" + qualifiedType + "." + memberName + "Property";
            if (symbols.ContainsKey(propertyField))
            {
                return new ResolvedSymbol(
                    propertyField,
                    memberName,
                    SemanticSymbolKind.Field,
                    NavigationPrecision.Precise,
                    null);
            }

            foreach (var methodName in new[] { "Get" + memberName, "Set" + memberName })
            {
                var prefix = "dotnet M:" + qualifiedType + "." + methodName;
                var method = symbols.Keys.FirstOrDefault(id =>
                    id.Equals(prefix, StringComparison.Ordinal) ||
                    id.StartsWith(prefix + "(", StringComparison.Ordinal));
                if (method is not null)
                {
                    return new ResolvedSymbol(
                        method,
                        memberName,
                        SemanticSymbolKind.Method,
                        NavigationPrecision.Precise,
                        null);
                }
            }
        }

        return new ResolvedSymbol(
            $"xaml-member {owner.Id} {memberName}",
            memberName,
            SemanticSymbolKind.Property,
            NavigationPrecision.Heuristic,
            null);
    }

    private static IEnumerable<string> TypeAndBases(
        string typeId,
        IReadOnlyDictionary<string, string[]> baseTypes)
    {
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(typeId);
        while (pending.TryDequeue(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;
            if (baseTypes.TryGetValue(current, out var bases))
            {
                foreach (var item in bases)
                {
                    pending.Enqueue(item);
                }
            }
        }
    }

    private static string EnsureSymbol(
        Dictionary<string, SemanticSymbol> symbols,
        string id,
        string displayName,
        SemanticSymbolKind kind)
    {
        symbols.TryAdd(id, new SemanticSymbol
        {
            Id = id,
            DisplayName = displayName,
            Kind = kind,
        });
        return id;
    }

    private static void AddOccurrence(
        List<SemanticOccurrence> occurrences,
        string path,
        SourceRange range,
        string symbolId,
        SemanticOccurrenceRoles roles,
        NavigationPrecision precision)
    {
        if (occurrences.Any(item =>
                item.DocumentPath == path && item.Range == range && item.SymbolId == symbolId))
        {
            return;
        }

        occurrences.Add(new SemanticOccurrence
        {
            DocumentPath = path,
            Range = range,
            SymbolId = symbolId,
            Roles = roles,
            Precision = precision,
        });
    }

    private static SourceRange Subrange(
        SourceText text,
        SourceRange valueRange,
        int offset,
        int length)
    {
        var line = text.Lines[valueRange.StartLine];
        var start = line.Start + valueRange.StartCharacter + offset;
        var span = text.Lines.GetLinePositionSpan(TextSpan.FromBounds(start, start + length));
        return new SourceRange(
            span.Start.Line,
            span.Start.Character,
            span.End.Line,
            span.End.Character);
    }

    private static (string Prefix, string Name) SplitName(string name)
    {
        var colon = name.IndexOf(':');
        return colon < 0 ? (string.Empty, name) : (name[..colon], name[(colon + 1)..]);
    }

    private static string? ClrNamespace(string? namespaceUri)
    {
        if (namespaceUri is null)
        {
            return null;
        }

        const string clrPrefix = "clr-namespace:";
        const string usingPrefix = "using:";
        var prefix = namespaceUri.StartsWith(clrPrefix, StringComparison.Ordinal)
            ? clrPrefix
            : namespaceUri.StartsWith(usingPrefix, StringComparison.Ordinal)
                ? usingPrefix
                : null;
        if (prefix is null)
        {
            return null;
        }

        var value = namespaceUri[prefix.Length..];
        var separator = value.IndexOf(';');
        return separator < 0 ? value : value[..separator];
    }

    private static string? PresentationType(string name, XamlDialect dialect) => dialect switch
    {
        XamlDialect.Wpf => name switch
        {
            "Window" => "System.Windows.Window",
            "Application" => "System.Windows.Application",
            "ResourceDictionary" => "System.Windows.ResourceDictionary",
            "Style" => "System.Windows.Style",
            "Setter" => "System.Windows.Setter",
            _ when IsCommonControl(name) => "System.Windows.Controls." + name,
            _ => null,
        },
        XamlDialect.WinUi => name switch
        {
            "Window" => "Microsoft.UI.Xaml.Window",
            "Application" => "Microsoft.UI.Xaml.Application",
            "ResourceDictionary" => "Microsoft.UI.Xaml.ResourceDictionary",
            "Style" => "Microsoft.UI.Xaml.Style",
            "Setter" => "Microsoft.UI.Xaml.Setter",
            _ when IsCommonControl(name) => "Microsoft.UI.Xaml.Controls." + name,
            _ => null,
        },
        XamlDialect.Maui => name switch
        {
            "Application" or "ResourceDictionary" or "Style" or "Setter" or
            "ContentPage" or "ContentView" or "Shell" or "FlyoutPage" or
            "NavigationPage" or "TabbedPage" or "Grid" or "Button" or "Entry" or
            "Editor" or "Label" or "Border" or "StackLayout" or "VerticalStackLayout" or
            "HorizontalStackLayout" or "AbsoluteLayout" or "FlexLayout" or "ScrollView" or
            "CollectionView" or "ListView" or "Picker" or "CheckBox" or "RadioButton" or
            "Image" or "WebView" => "Microsoft.Maui.Controls." + name,
            _ => null,
        },
        XamlDialect.Avalonia => name switch
        {
            "Application" => "Avalonia.Application",
            "ResourceDictionary" => "Avalonia.Controls.ResourceDictionary",
            "Style" => "Avalonia.Styling.Style",
            "Setter" => "Avalonia.Styling.Setter",
            "Window" or "UserControl" or "TemplatedControl" or "Grid" or "Button" or
            "TextBox" or "TextBlock" or "Label" or "Border" or "StackPanel" or
            "DockPanel" or "Canvas" or "ContentControl" or "ItemsControl" or "ListBox" or
            "ComboBox" or "CheckBox" or "RadioButton" or "Menu" or "DataGrid" or
            "ScrollViewer" or "Image" => "Avalonia.Controls." + name,
            _ => null,
        },
        _ => null,
    };

    private static bool IsCommonControl(string name) => name is
        "Grid" or "Button" or "TextBox" or "TextBlock" or "Label" or "Border" or
        "StackPanel" or "DockPanel" or "Canvas" or "ContentControl" or "UserControl" or
        "ItemsControl" or "ListBox" or "ComboBox" or "CheckBox" or "RadioButton" or
        "Menu" or "DataGrid";

    private static XamlDialect DetectDialect(
        XamlSyntaxDocument syntax,
        IReadOnlyDictionary<string, SemanticSymbol> symbols,
        IReadOnlyDictionary<string, string[]> baseTypes,
        string? xamlClass)
    {
        var defaultNamespace = syntax.Elements.FirstOrDefault()?.Namespaces
            .GetValueOrDefault(string.Empty);
        if (string.Equals(defaultNamespace, MauiNamespace, StringComparison.Ordinal))
        {
            return XamlDialect.Maui;
        }

        if (string.Equals(defaultNamespace, AvaloniaNamespace, StringComparison.Ordinal))
        {
            return XamlDialect.Avalonia;
        }

        if (string.Equals(defaultNamespace, PresentationNamespace, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(xamlClass))
            {
                var classId = "dotnet T:" + xamlClass;
                var ancestors = TypeAndBases(classId, baseTypes).Skip(1).ToArray();
                if (ancestors.Any(id =>
                        id.StartsWith("dotnet T:Microsoft.UI.Xaml.", StringComparison.Ordinal)))
                {
                    return XamlDialect.WinUi;
                }

                if (ancestors.Any(id =>
                        id.StartsWith("dotnet T:System.Windows.", StringComparison.Ordinal)))
                {
                    return XamlDialect.Wpf;
                }
            }

            var hasWinUi = symbols.Keys.Any(id =>
                id.StartsWith("dotnet T:Microsoft.UI.Xaml.", StringComparison.Ordinal));
            var hasWpf = symbols.Keys.Any(id =>
                id.StartsWith("dotnet T:System.Windows.", StringComparison.Ordinal));
            if (hasWinUi && !hasWpf)
            {
                return XamlDialect.WinUi;
            }
        }

        return XamlDialect.Wpf;
    }

    private static bool IsFrameworkNamespace(string? namespaceUri, XamlDialect dialect) =>
        dialect switch
        {
            XamlDialect.Wpf or XamlDialect.WinUi =>
                string.Equals(namespaceUri, PresentationNamespace, StringComparison.Ordinal),
            XamlDialect.Maui => string.Equals(namespaceUri, MauiNamespace, StringComparison.Ordinal),
            XamlDialect.Avalonia => string.Equals(namespaceUri, AvaloniaNamespace, StringComparison.Ordinal),
            _ => false,
        };

    private static string NameId(
        string path,
        string name,
        string? xamlClass,
        IReadOnlyDictionary<string, SemanticSymbol> symbols)
    {
        if (!string.IsNullOrWhiteSpace(xamlClass))
        {
            var generatedField = "dotnet F:" + xamlClass + "." + name;
            if (symbols.ContainsKey(generatedField))
            {
                return generatedField;
            }
        }

        return $"xaml-name {path}#{name}";
    }
    private static string ResourceId(string path, string name) => $"xaml-resource {path}#{name}";

    private static (string Id, NavigationPrecision Precision) ResolveResource(
        string documentPath,
        string name,
        IReadOnlyDictionary<string, string[]> definitions)
    {
        if (definitions.TryGetValue(name, out var paths))
        {
            if (paths.Contains(documentPath, StringComparer.Ordinal))
            {
                return (ResourceId(documentPath, name), NavigationPrecision.Inferred);
            }

            if (paths.Length == 1)
            {
                return (ResourceId(paths[0], name), NavigationPrecision.Inferred);
            }
        }

        return ($"xaml-resource-unresolved {name}", NavigationPrecision.Heuristic);
    }

    private static bool IsXamlAttribute(
        XamlElementSyntax element,
        XamlAttributeSyntax attribute,
        string localName)
    {
        var (prefix, name) = SplitName(attribute.Name);
        return string.Equals(name, localName, StringComparison.Ordinal) &&
               element.Namespaces.TryGetValue(prefix, out var uri) &&
               string.Equals(uri, XamlNamespace, StringComparison.Ordinal);
    }
    private static string LastSegment(string value) => value[(value.LastIndexOf('.') + 1)..];

    private sealed record ResolvedSymbol(
        string Id,
        string DisplayName,
        SemanticSymbolKind Kind,
        NavigationPrecision Precision,
        string? QualifiedType);

    private enum XamlDialect : byte
    {
        Wpf,
        WinUi,
        Maui,
        Avalonia,
    }
}
