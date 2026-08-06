using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Semantics;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Tests;

public sealed class XamlSemanticIndexerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-xaml-indexer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolves_local_types_properties_and_attached_properties_to_csharp()
    {
        const string xaml =
            """
            <local:MainWindow xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                              xmlns:local="clr-namespace:Demo"
                              x:Class="Demo.MainWindow"
                              Title="Hello"
                              Activated="OnActivated"
                              local:Flags.Flag="yes" />
            """;
        WriteXaml(xaml);
        var index = new XamlSemanticIndexer().Supplement(CSharpIndex(), _root);
        var navigation = new SemanticNavigationService(index);
        var snapshot = Snapshot();

        var element = Position(xaml, "local:MainWindow");
        var title = Position(xaml, "Title=\"");
        var attached = Position(xaml, "local:Flags.Flag");
        var activated = Position(xaml, "Activated=\"");
        var eventHandler = Position(xaml, "OnActivated\"");

        Assert.Contains(
            navigation.GoToDefinition("Views/Main.xaml", element.Line, element.Character, snapshot),
            location => location.DocumentPath == "Source.cs" && location.Range.StartLine == 1);
        Assert.Equal(
            new SourceRange(2, 15, 2, 20),
            Assert.Single(navigation.GoToDefinition(
                "Views/Main.xaml", title.Line, title.Character, snapshot)).Range);
        Assert.Equal(
            new SourceRange(3, 22, 3, 29),
            Assert.Single(navigation.GoToDefinition(
                "Views/Main.xaml", attached.Line, attached.Character, snapshot)).Range);
        Assert.Equal(
            new SourceRange(6, 17, 6, 26),
            Assert.Single(navigation.GoToDefinition(
                "Views/Main.xaml", activated.Line, activated.Character, snapshot)).Range);
        Assert.Equal(
            new SourceRange(7, 21, 7, 32),
            Assert.Single(navigation.GoToDefinition(
                "Views/Main.xaml", eventHandler.Line, eventHandler.Character, snapshot)).Range);
    }

    [Fact]
    public void Resolves_xaml_names_and_same_dictionary_static_resources()
    {
        const string xaml =
            """
            <local:MainWindow xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                              xmlns:local="clr-namespace:Demo"
                              x:Class="Demo.MainWindow"
                              x:Name="Root">
              <local:Card x:Key="CardStyle" Tag="{x:Reference Root}" />
              <local:Card Style="{StaticResource CardStyle}" Text="{Binding Path=Caption}" />
            </local:MainWindow>
            """;
        WriteXaml(xaml);
        var index = new XamlSemanticIndexer().Supplement(CSharpIndex(), _root);
        var navigation = new SemanticNavigationService(index);
        var snapshot = Snapshot();
        var nameReference = Position(xaml, "Root}\"");
        var resourceReference = Position(xaml, "CardStyle}\"");
        var binding = Position(xaml, "Caption}\"");

        var nameDefinition = Assert.Single(navigation.GoToDefinition(
            "Views/Main.xaml", nameReference.Line, nameReference.Character, snapshot));
        var resourceDefinition = Assert.Single(navigation.GoToDefinition(
            "Views/Main.xaml", resourceReference.Line, resourceReference.Character, snapshot));

        Assert.Equal(Position(xaml, "Root\""), Start(nameDefinition.Range));
        Assert.Equal(Position(xaml, "CardStyle\""), Start(resourceDefinition.Range));
        Assert.Equal(NavigationPrecision.Inferred,
            navigation.ResolveOccurrence(
                "Views/Main.xaml",
                resourceReference.Line,
                resourceReference.Character,
                snapshot)!.Precision);
        Assert.Equal(NavigationPrecision.Heuristic,
            navigation.ResolveOccurrence(
                "Views/Main.xaml",
                binding.Line,
                binding.Character,
                snapshot)!.Precision);
        Assert.Empty(navigation.GoToDefinition(
            "Views/Main.xaml", binding.Line, binding.Character, snapshot));

        var generatedFieldReference = navigation.GoToDefinition(
            "Source.cs", 5, 10, snapshot);
        Assert.Equal(
            Position(xaml, "Root\""),
            Start(Assert.Single(generatedFieldReference).Range));
    }

    [Fact]
    public void Resolves_a_unique_resource_across_merged_dictionary_files()
    {
        const string dictionary =
            """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="AccentStyle" />
            </ResourceDictionary>
            """;
        const string view =
            """
            <Button xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Style="{StaticResource AccentStyle}" />
            """;
        var dictionaries = Path.Combine(_root, "Dictionaries");
        var views = Path.Combine(_root, "Views");
        Directory.CreateDirectory(dictionaries);
        Directory.CreateDirectory(views);
        File.WriteAllText(Path.Combine(dictionaries, "Colors.xaml"), dictionary);
        File.WriteAllText(Path.Combine(views, "Main.xaml"), view);

        var index = new XamlSemanticIndexer().Supplement(CSharpIndex(), _root);
        var reference = Position(view, "AccentStyle}");
        var definition = Assert.Single(new SemanticNavigationService(index).GoToDefinition(
            "Views/Main.xaml",
            reference.Line,
            reference.Character,
            Snapshot()));

        Assert.Equal("Dictionaries/Colors.xaml", definition.DocumentPath);
        Assert.Equal(Position(dictionary, "AccentStyle\""), Start(definition.Range));
        Assert.Equal(NavigationPrecision.Precise, definition.Precision);
    }

    [Theory]
    [InlineData(
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
        "using:Demo",
        "dotnet T:Microsoft.UI.Xaml.Controls.Button")]
    [InlineData(
        "http://schemas.microsoft.com/dotnet/2021/maui",
        "clr-namespace:Demo",
        "dotnet T:Microsoft.Maui.Controls.Button")]
    [InlineData(
        "https://github.com/avaloniaui",
        "using:Demo",
        "dotnet T:Avalonia.Controls.Button")]
    public void Resolves_winui_maui_and_avalonia_dialects(
        string frameworkNamespace,
        string localNamespace,
        string buttonId)
    {
        var xaml =
            $"""
             <Button xmlns="{frameworkNamespace}"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="{localNamespace}"
                     x:DataType="local:ViewModel"
                     local:Card.Flag="yes" />
             """;
        WriteXaml(xaml);
        var index = new XamlSemanticIndexer().Supplement(
            PlatformIndex(
                Symbol(buttonId, "Button", SemanticSymbolKind.Type),
                Symbol("dotnet T:Demo.ViewModel", "ViewModel", SemanticSymbolKind.Type),
                Symbol("dotnet T:Demo.Card", "Card", SemanticSymbolKind.Type),
                Symbol("dotnet F:Demo.Card.FlagProperty", "FlagProperty", SemanticSymbolKind.Field)),
            _root);
        var navigation = new SemanticNavigationService(index);

        Assert.Equal(
            buttonId,
            navigation.ResolveOccurrence(
                "Views/Main.xaml",
                Position(xaml, "Button").Line,
                Position(xaml, "Button").Character,
                Snapshot())!.SymbolId);
        Assert.Equal(
            "dotnet T:Demo.ViewModel",
            navigation.ResolveOccurrence(
                "Views/Main.xaml",
                Position(xaml, "local:ViewModel").Line,
                Position(xaml, "local:ViewModel").Character,
                Snapshot())!.SymbolId);
        var attached = navigation.ResolveOccurrence(
            "Views/Main.xaml",
            Position(xaml, "local:Card.Flag").Line,
            Position(xaml, "local:Card.Flag").Character,
            Snapshot());
        Assert.Equal("dotnet F:Demo.Card.FlagProperty", attached!.SymbolId);
        Assert.Equal(NavigationPrecision.Precise, attached.Precision);
    }

    [Theory]
    [InlineData(
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
        "{x:Bind Caption}",
        "dotnet T:Microsoft.UI.Xaml.Controls.Button")]
    [InlineData(
        "http://schemas.microsoft.com/dotnet/2021/maui",
        "{Binding Path=Caption}",
        "dotnet T:Microsoft.Maui.Controls.Button")]
    [InlineData(
        "https://github.com/avaloniaui",
        "{CompiledBinding Caption}",
        "dotnet T:Avalonia.Controls.Button")]
    public void Recognizes_platform_binding_syntax_as_explicitly_heuristic(
        string frameworkNamespace,
        string binding,
        string buttonId)
    {
        var xaml =
            $"""
             <Button xmlns="{frameworkNamespace}"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     Text="{binding}" />
             """;
        WriteXaml(xaml);
        var index = new XamlSemanticIndexer().Supplement(
            PlatformIndex(Symbol(buttonId, "Button", SemanticSymbolKind.Type)),
            _root);
        var position = Position(xaml, "Caption");
        var occurrence = new SemanticNavigationService(index).ResolveOccurrence(
            "Views/Main.xaml", position.Line, position.Character, Snapshot());

        Assert.NotNull(occurrence);
        Assert.Equal("xaml-binding Caption", occurrence.SymbolId);
        Assert.Equal(NavigationPrecision.Heuristic, occurrence.Precision);
    }

    private void WriteXaml(string text)
    {
        var directory = Path.Combine(_root, "Views");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Main.xaml"), text);
    }

    private static SemanticIndex CSharpIndex()
    {
        var symbols = new[]
        {
            Symbol("dotnet T:Demo.MainWindow", "MainWindow", SemanticSymbolKind.Type),
            Symbol("dotnet P:Demo.MainWindow.Title", "Title", SemanticSymbolKind.Property),
            Symbol("dotnet M:Demo.Flags.GetFlag(System.Object)", "GetFlag", SemanticSymbolKind.Method),
            Symbol("dotnet T:Demo.Card", "Card", SemanticSymbolKind.Type),
            Symbol("dotnet F:Demo.MainWindow.Root", "Root", SemanticSymbolKind.Field),
            Symbol("dotnet E:Demo.MainWindow.Activated", "Activated", SemanticSymbolKind.Event),
            Symbol("dotnet M:Demo.MainWindow.OnActivated(System.Object,System.EventArgs)",
                "OnActivated", SemanticSymbolKind.Method),
        };
        return new SemanticIndex
        {
            RepositoryId = "repo",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            BaseCommit = "commit",
            IndexedAtUtc = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                new SemanticDocument
                {
                    RelPath = "Source.cs",
                    Hash = SHA256.HashData(Encoding.UTF8.GetBytes("source")),
                },
            ],
            Symbols = symbols.ToList(),
            Occurrences =
            [
                Definition("dotnet T:Demo.MainWindow", 1, 13, 23),
                Definition("dotnet P:Demo.MainWindow.Title", 2, 15, 20),
                Definition("dotnet M:Demo.Flags.GetFlag(System.Object)", 3, 22, 29),
                Definition("dotnet T:Demo.Card", 4, 13, 17),
                new SemanticOccurrence
                {
                    DocumentPath = "Source.cs",
                    Range = new SourceRange(5, 10, 5, 14),
                    SymbolId = "dotnet F:Demo.MainWindow.Root",
                    Roles = SemanticOccurrenceRoles.Reference,
                    Precision = NavigationPrecision.Precise,
                },
                Definition("dotnet E:Demo.MainWindow.Activated", 6, 17, 26),
                Definition(
                    "dotnet M:Demo.MainWindow.OnActivated(System.Object,System.EventArgs)",
                    7,
                    21,
                    32),
            ],
            Relationships = [],
        };
    }

    private static SemanticIndex PlatformIndex(params SemanticSymbol[] symbols) =>
        new()
        {
            RepositoryId = "repo",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            BaseCommit = "commit",
            IndexedAtUtc = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                new SemanticDocument
                {
                    RelPath = "Source.cs",
                    Hash = SHA256.HashData(Encoding.UTF8.GetBytes("platform source")),
                },
            ],
            Symbols = symbols.ToList(),
            Occurrences = symbols.Select((symbol, line) => new SemanticOccurrence
            {
                DocumentPath = "Source.cs",
                Range = new SourceRange(line, 0, line, symbol.DisplayName.Length),
                SymbolId = symbol.Id,
                Roles = SemanticOccurrenceRoles.Definition,
                Precision = NavigationPrecision.Precise,
            }).ToList(),
            Relationships = [],
        };

    private static SemanticSymbol Symbol(string id, string name, SemanticSymbolKind kind) =>
        new() { Id = id, DisplayName = name, Kind = kind };

    private static SemanticOccurrence Definition(string id, int line, int start, int end) =>
        new()
        {
            DocumentPath = "Source.cs",
            Range = new SourceRange(line, start, line, end),
            SymbolId = id,
            Roles = SemanticOccurrenceRoles.Definition,
            Precision = NavigationPrecision.Precise,
        };

    private static SemanticSnapshotIdentity Snapshot() =>
        new("repo", "generation", "tree", null);

    private static LinePosition Position(string source, string value)
    {
        var offset = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{value}' was not found.");
        return SourceText.From(source).Lines.GetLinePosition(offset);
    }

    private static LinePosition Start(SourceRange range) =>
        new(range.StartLine, range.StartCharacter);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
