using CodeSearch.Core.Chunking;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class SymbolAwareChunkerTests
{
    private const string Module = """
        \"\"\"Module docstring.\"\"\"

        import functools


        def apply_tax(amount):
            return amount * 1.2


        class Invoice:
            def add(self, amount):
                self.lines.append(amount)

            def total(self):
                return apply_tax(0)


        INVOICE = Invoice()
        print(INVOICE.total())
        """;

    [Fact]
    public void Gives_every_definition_its_own_chunk_named_for_the_symbol()
    {
        var chunks = Split(Module, Definitions());

        var symbols = chunks.Select(chunk => chunk.Symbol).ToArray();
        Assert.Contains("apply_tax", symbols);
        Assert.Contains("Invoice", symbols);
        // A method is named for the type that holds it, the way a C# member chunk is.
        Assert.Contains("Invoice.add", symbols);
        Assert.Contains("Invoice.total", symbols);

        var applyTax = chunks.Single(chunk => chunk.Symbol == "apply_tax");
        Assert.Equal(ChunkKind.Method, applyTax.Kind);
        Assert.Equal("def apply_tax(amount):", applyTax.Signature);
        Assert.Equal(6, applyTax.StartLine);
        Assert.Equal(7, applyTax.EndLine);

        var invoice = chunks.Single(chunk => chunk.Symbol == "Invoice");
        Assert.Equal(ChunkKind.Type, invoice.Kind);
    }

    [Fact]
    public void Covers_every_line_exactly_once()
    {
        var lines = SourceLines.Split(Module);
        var chunks = Split(Module, Definitions());

        var owners = new Dictionary<int, List<string>>();
        foreach (var chunk in chunks)
        {
            for (var line = chunk.StartLine; line <= chunk.EndLine; line++)
            {
                // A nested definition's own lines belong to the nested chunk. Its first line is
                // listed in the parent as a table-of-contents entry, exactly as the Roslyn
                // chunker lists a type's members, so the parent's span still spells the type.
                if (chunks.Any(other =>
                        other != chunk &&
                        other.StartLine <= line &&
                        other.EndLine >= line &&
                        other.EndLine - other.StartLine < chunk.EndLine - chunk.StartLine))
                {
                    continue;
                }

                owners.TryAdd(line, []);
                owners[line].Add(chunk.Symbol);
            }
        }

        for (var line = 1; line <= lines.Length; line++)
        {
            if (string.IsNullOrWhiteSpace(lines[line - 1]))
            {
                continue;
            }

            Assert.True(
                owners.ContainsKey(line),
                $"Line {line} is in no chunk: {lines[line - 1]}");
            Assert.True(
                owners[line].Count == 1,
                $"Line {line} is in {owners[line].Count} chunks: {string.Join(", ", owners[line])}");
        }
    }

    [Fact]
    public void Keeps_the_window_for_the_regions_no_definition_covers()
    {
        var chunks = Split(Module, Definitions());

        var text = chunks.Where(chunk => chunk.Kind == ChunkKind.Text).ToArray();

        // The docstring and the import above the first definition, and the module-level code
        // after the last one. Neither belongs to a symbol, and neither may go unindexed.
        Assert.Contains(text, chunk => chunk.StartLine == 1);
        Assert.Contains(text, chunk => chunk.EndLine == SourceLines.Split(Module).Length);
        Assert.All(text, chunk => Assert.StartsWith("module.py", chunk.Symbol, StringComparison.Ordinal));
    }

    [Fact]
    public void Ignores_a_body_that_claims_the_whole_file()
    {
        // scip-typescript reports the module symbol's body as the entire document. Kept, it
        // would nest every real definition inside one chunk containing everything.
        var definitions = Definitions()
            .Prepend(new SymbolDefinition(
                new SourceRange(0, 0, 0, 0),
                new SourceRange(0, 0, SourceLines.Split(Module).Length - 1, 0)))
            .ToList();

        var chunks = Split(Module, definitions);

        Assert.DoesNotContain(chunks, chunk =>
            chunk.Kind != ChunkKind.Text &&
            chunk.StartLine == 1 &&
            chunk.EndLine >= SourceLines.Split(Module).Length - 1);
    }

    [Fact]
    public void Falls_back_to_the_window_when_nothing_reported_a_body()
    {
        // Everything a file declares can sit inside a function body, and scip-typescript reports
        // no span for any of it. That file must chunk exactly as it did before this existed.
        var chunker = new SymbolAwareChunker([]);

        var chunks = chunker.Split("module.py", Module).ToList();
        var expected = new GenericTextChunker().Split("module.py", Module).ToList();

        Assert.Equal(
            expected.Select(chunk => (chunk.Symbol, chunk.StartLine, chunk.EndLine)),
            chunks.Select(chunk => (chunk.Symbol, chunk.StartLine, chunk.EndLine)));
    }

    [Fact]
    public void Splits_a_definition_too_large_for_one_vector_and_keeps_its_name()
    {
        var body = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"    value_{i} = {i}"));
        var content = "def enormous():\n" + body;
        var definitions = new List<SymbolDefinition>
        {
            new(new SourceRange(0, 4, 0, 12), new SourceRange(0, 0, 400, 0)),
        };

        var chunks = Split(content, definitions);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
            Assert.True(chunk.EmbedText.Length <= ChunkLimits.MaxChars));
        Assert.All(chunks, chunk =>
            Assert.StartsWith("enormous", chunk.Symbol, StringComparison.Ordinal));
        Assert.Contains(chunks, chunk => chunk.Symbol.Contains("[1/", StringComparison.Ordinal));
    }

    [Fact]
    public void Infers_the_extent_of_a_declaration_that_reported_no_body()
    {
        // What scip-typescript hands over for Component.tsx: a body for the function declaration,
        // and a bare name for the two whose initialiser is a call.
        var chunks = Split("Component.tsx", Component, ComponentDefinitions());

        var sidebar = chunks.Single(chunk => chunk.Symbol == "Sidebar");
        Assert.Equal("export const Sidebar = memo(() => {", sidebar.Signature);
        // Runs to the line before the next thing the indexer named, with the blank line after it
        // left to the window rather than padding the chunk.
        Assert.Equal(5, sidebar.StartLine);
        Assert.Equal(8, sidebar.EndLine);
        Assert.Contains("useState", sidebar.EmbedText, StringComparison.Ordinal);

        // The declaration that did report a body is unaffected by any of this.
        var helper = chunks.Single(chunk => chunk.Symbol == "helper");
        Assert.Equal(10, helper.StartLine);
        Assert.Equal(12, helper.EndLine);
    }

    [Fact]
    public void Leaves_a_one_line_declaration_to_the_window()
    {
        // `const access = 'public' as const;` is a declaration by the indexer's reckoning and not
        // a body by anyone's. It stays in the region it is written in.
        var chunks = Split("Component.tsx", Component, ComponentDefinitions());

        Assert.DoesNotContain(chunks, chunk => chunk.Symbol == "access");
        Assert.Contains(chunks, chunk =>
            chunk.Kind == ChunkKind.Text && chunk.StartLine <= 3 && chunk.EndLine >= 3);
    }

    [Fact]
    public void Keeps_a_declaration_inside_a_reported_body_with_the_body_that_holds_it()
    {
        // A name the indexer reports no body for, written inside one that has a body, cannot have
        // its extent read off the next top-level declaration. It stays in its parent's chunk.
        var definitions = ComponentDefinitions()
            .Append(new SymbolDefinition(new SourceRange(10, 9, 10, 14), null))
            .ToList();

        var chunks = Split("Component.tsx", Component, definitions);

        Assert.DoesNotContain(chunks, chunk => chunk.Symbol.StartsWith("value", StringComparison.Ordinal));
        var helper = chunks.Single(chunk => chunk.Symbol == "helper");
        Assert.Equal(10, helper.StartLine);
        Assert.Equal(12, helper.EndLine);
    }

    [Fact]
    public void Falls_back_to_the_window_when_every_inferred_extent_is_one_line()
    {
        // One-line declarations and nothing else: there is no boundary worth cutting on, and the
        // file has to chunk exactly as it did before any of this existed.
        var content = "const a = f();\nconst b = g();\n";
        var definitions = new List<SymbolDefinition>
        {
            new(new SourceRange(0, 6, 0, 7), null),
            new(new SourceRange(1, 6, 1, 7), null),
        };

        var chunks = new SymbolAwareChunker(definitions).Split("consts.ts", content).ToList();
        var expected = new GenericTextChunker().Split("consts.ts", content).ToList();

        Assert.Equal(
            expected.Select(chunk => (chunk.Symbol, chunk.StartLine, chunk.EndLine)),
            chunks.Select(chunk => (chunk.Symbol, chunk.StartLine, chunk.EndLine)));
    }

    private static List<Chunk> Split(string content, IReadOnlyList<SymbolDefinition> definitions) =>
        Split("module.py", content, definitions);

    private static List<Chunk> Split(
        string relPath,
        string content,
        IReadOnlyList<SymbolDefinition> definitions) =>
        new SymbolAwareChunker(definitions).Split(relPath, content).ToList();

    private const string Component = """
        import { memo, useState } from 'react';

        const access = 'public' as const;

        export const Sidebar = memo(() => {
          const [open, setOpen] = useState(false);
          return <div onClick={() => setOpen(!open)}>{access}</div>;
        });

        export function helper(value: number) {
          return value * 2;
        }
        """;

    /// <summary>
    /// The two shapes scip-typescript reports no <c>enclosing_range</c> for, beside one it does.
    /// </summary>
    private static List<SymbolDefinition> ComponentDefinitions() =>
    [
        // const access = …      → line 3, no body reported
        new(new SourceRange(2, 6, 2, 12), null),
        // export const Sidebar = memo(…)  → line 5, no body reported
        new(new SourceRange(4, 13, 4, 20), null),
        // export function helper(…)       → lines 10-12
        new(new SourceRange(9, 16, 9, 22), new SourceRange(9, 0, 11, 1)),
    ];

    /// <summary>
    /// What the importer would produce for <see cref="Module"/>: a body span per definition, and
    /// the name range that spells its identifier.
    /// </summary>
    private static List<SymbolDefinition> Definitions() =>
    [
        // def apply_tax(amount):  → lines 6-7 (zero-based 5-6)
        new(new SourceRange(5, 4, 5, 13), new SourceRange(5, 0, 6, 25)),
        // class Invoice:          → lines 10-15
        new(new SourceRange(9, 6, 9, 13), new SourceRange(9, 0, 14, 28)),
        // def add(self, amount):  → lines 11-12
        new(new SourceRange(10, 8, 10, 11), new SourceRange(10, 4, 11, 32)),
        // def total(self):        → lines 14-15
        new(new SourceRange(13, 8, 13, 13), new SourceRange(13, 4, 14, 28)),
    ];
}
