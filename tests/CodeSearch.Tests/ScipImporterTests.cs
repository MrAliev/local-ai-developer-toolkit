using System.Text;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class ScipImporterTests
{
    private const string FunctionSymbol = "scip-python python demo 1.0 module/foo().";

    [Fact]
    public void ImportsUtf32DefinitionsAndReferencesForNavigation()
    {
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "src/demo.py");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 2, 5);
                occurrence.String(2, FunctionSymbol);
                occurrence.Int32(3, 1);
            });
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 1, 0, 3);
                occurrence.String(2, FunctionSymbol);
                occurrence.Int32(3, 8);
            });
            document.Message(3, symbol =>
            {
                symbol.String(1, FunctionSymbol);
                symbol.Int32(5, 17);
                symbol.String(6, "foo");
            });
            document.String(5, "🚀 foo\nfoo()");
            document.Int32(6, 3);
        }));

        var imported = Import(payload);
        var navigation = new SemanticNavigationService(imported);
        var definition = Assert.Single(navigation.GoToDefinition(
            "src/demo.py", 1, 1, Snapshot()));
        var references = navigation.FindReferences(
            "src/demo.py", 1, 1, includeDefinition: false, Snapshot());

        Assert.Equal(new SourceRange(0, 3, 0, 6), definition.Range);
        Assert.Equal(SemanticSymbolKind.Method, Assert.Single(imported.Symbols).Kind);
        Assert.Equal(new SourceRange(1, 0, 1, 3), Assert.Single(references).Range);
    }

    [Theory]
    [InlineData(1, 4, 7)]
    [InlineData(3, 1, 4)]
    public void ConvertsUtf8AndUtf32OffsetsToUtf16(int encoding, int start, int end)
    {
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "unicode.py");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, start, end);
                occurrence.String(2, FunctionSymbol);
                occurrence.Int32(3, 1);
            });
            document.String(5, "🚀foo");
            document.Int32(6, encoding);
        }));

        var occurrence = Assert.Single(Import(payload).Occurrences);

        Assert.Equal(new SourceRange(0, 2, 0, 5), occurrence.Range);
    }

    [Fact]
    public void ScopesLocalSymbolsToTheirDocument()
    {
        var payload = Index(index =>
        {
            AddLocalDocument(index, "a.py");
            AddLocalDocument(index, "b.py");
        });

        var imported = Import(payload);

        Assert.Equal(2, imported.Symbols.Count);
        Assert.Equal(2, imported.Symbols.Select(symbol => symbol.Id).Distinct().Count());
        Assert.Contains(imported.Symbols, symbol => symbol.Id == "scip-local a.py 0");
        Assert.Contains(imported.Symbols, symbol => symbol.Id == "scip-local b.py 0");
    }

    [Fact]
    public void IgnoresUnscopedExternalLocalSymbolsAndKeepsDocumentLocalNavigation()
    {
        var payload = Index(index =>
        {
            index.Message(3, symbol =>
            {
                symbol.String(1, "local 0");
                symbol.String(3, "documentation emitted by scip-python");
            });
            AddLocalDocument(index, "candidate.py");
        });

        var imported = Import(payload);
        var occurrence = Assert.Single(imported.Occurrences);
        var symbol = Assert.Single(imported.Symbols);

        Assert.Equal("scip-local candidate.py 0", occurrence.SymbolId);
        Assert.Equal(occurrence.SymbolId, symbol.Id);
    }

    [Fact]
    public void AcceptsTheLocalIdShapeScipPythonActuallyEmits()
    {
        // scip-python 0.6.6 appends the variable name in parentheses: `local 16(line)`. The SCIP
        // grammar does not allow that, and rejecting it failed the python adapter — which blocks
        // the whole generation, so every Python repository was left unable to publish an index.
        var payload = Index(index => AddLocalDocument(index, "app.py", "local 16(line)"));

        var imported = Import(payload);

        var symbol = Assert.Single(imported.Symbols);
        Assert.Equal("scip-local app.py 16(line)", symbol.Id);
        Assert.Equal(symbol.Id, Assert.Single(imported.Occurrences).SymbolId);
    }

    [Theory]
    [InlineData("local 0 1")]
    [InlineData("local a\tb")]
    [InlineData("local a\nb")]
    public void RejectsLocalIdsThatWouldCollideOnceScopedToADocument(string symbol)
    {
        // The id is joined to its document path by a space, so whitespace is the one thing that
        // can make two distinct symbols compose the same key.
        var payload = Index(index => AddLocalDocument(index, "app.py", symbol));

        var error = Assert.Throws<InvalidDataException>(() => Import(payload));

        Assert.Contains("local symbol ID is invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesTheOffendingLocalIdSoTheNextOneTakesMinutesNotHours()
    {
        var payload = Index(index => AddLocalDocument(index, "app.py", "local bad id"));

        var error = Assert.Throws<InvalidDataException>(() => Import(payload));

        Assert.Contains("bad?id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsTraversalPaths()
    {
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "../escape.py");
            document.String(5, "x");
            document.Int32(6, 2);
        }));

        Assert.Throws<InvalidDataException>(() => Import(payload));
    }

    [Fact]
    public void RejectsImplicitPositionEncoding()
    {
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "implicit.py");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 0, 1);
                occurrence.String(2, FunctionSymbol);
            });
            document.String(5, "x");
        }));

        Assert.Throws<InvalidDataException>(() => Import(payload));
    }

    [Fact]
    public void AcceptsAnExplicitLegacyEncodingFallback()
    {
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "legacy.ts");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 2, 5);
                occurrence.String(2, FunctionSymbol);
                occurrence.Int32(3, 1);
            });
            document.String(5, "🚀foo");
        }));

        var imported = new ScipImporter().Supplement(
            EmptyIndex(),
            new MemoryStream(payload),
            Path.GetTempPath(),
            ScipPositionEncoding.Utf16);

        Assert.Equal(
            new SourceRange(0, 2, 0, 5),
            Assert.Single(imported.Occurrences).Range);
    }

    [Fact]
    public void RejectsPayloadsOverTheConfiguredLimit()
    {
        var importer = new ScipImporter(new ScipImportLimits(
            MaximumBytes: 8,
            MaximumDocuments: 1,
            MaximumOccurrences: 1,
            MaximumSymbols: 1,
            MaximumStringBytes: 4));

        Assert.Throws<InvalidDataException>(() => importer.Supplement(
            EmptyIndex(), new MemoryStream(new byte[9]), Path.GetTempPath()));
    }

    [Fact]
    public void AppliesOccurrenceLimitsAcrossAllDocuments()
    {
        var payload = Index(index =>
        {
            AddLocalDocument(index, "a.py");
            AddLocalDocument(index, "b.py");
        });
        var importer = new ScipImporter(new ScipImportLimits(
            MaximumBytes: 4096,
            MaximumDocuments: 2,
            MaximumOccurrences: 1,
            MaximumSymbols: 2,
            MaximumStringBytes: 1024));

        Assert.Throws<InvalidDataException>(() => importer.Supplement(
            EmptyIndex(), new MemoryStream(payload), Path.GetTempPath()));
    }

    [Fact]
    public void RejectsGlobalSymbolsWithoutPackageAndVersionIdentity()
    {
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "bad.py");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 0, 1);
                occurrence.String(2, "not-a-canonical-symbol");
            });
            document.String(5, "x");
            document.Int32(6, 2);
        }));

        Assert.Throws<InvalidDataException>(() => Import(payload));
    }

    private static void AddLocalDocument(
        ProtoBuilder index,
        string path,
        string localSymbol = "local 0") =>
        index.Message(2, document =>
        {
            document.String(1, path);
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 0, 1);
                occurrence.String(2, localSymbol);
                occurrence.Int32(3, 1);
            });
            document.Message(3, symbol =>
            {
                symbol.String(1, localSymbol);
                symbol.Int32(5, 61);
                symbol.String(6, "x");
            });
            document.String(5, "x");
            document.Int32(6, 2);
        });

    [Fact]
    public void ImportsTheEnclosingRangeAsTheBodyOfADefinition()
    {
        // The name is on line 8; the body runs to the end of line 9. Both come from the same
        // occurrence, and until now only the first of them was read.
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "src/demo.py");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 4, 15);
                occurrence.String(2, FunctionSymbol);
                occurrence.Int32(3, 1);
                occurrence.PackedInt32(7, 0, 0, 1, 16);
            });
            document.String(5, "def round_money(value):\n    return value");
            document.Int32(6, 3);
        }));

        var occurrence = Assert.Single(Import(payload).Occurrences);

        Assert.Equal(new SourceRange(0, 4, 0, 15), occurrence.Range);
        Assert.Equal(new SourceRange(0, 0, 1, 16), occurrence.EnclosingRange);
    }

    [Fact]
    public void An_occurrence_the_indexer_reports_no_body_for_carries_none()
    {
        // scip-typescript does this for anything declared inside a function body, so "absent" is
        // an ordinary answer rather than a broken index.
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "src/demo.py");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 4, 7);
                occurrence.String(2, FunctionSymbol);
                occurrence.Int32(3, 1);
            });
            document.String(5, "def foo():\n    pass");
            document.Int32(6, 3);
        }));

        Assert.Null(Assert.Single(Import(payload).Occurrences).EnclosingRange);
    }

    [Fact]
    public void A_malformed_enclosing_range_is_dropped_rather_than_carried()
    {
        // Two integers are not a range in any encoding. Importing it as one would put a chunk
        // boundary somewhere nobody could explain; refusing the whole document over it would
        // lose navigation that is perfectly good.
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "src/demo.py");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 4, 7);
                occurrence.String(2, FunctionSymbol);
                occurrence.Int32(3, 1);
                occurrence.PackedInt32(7, 0, 4);
            });
            document.String(5, "def foo():\n    pass");
            document.Int32(6, 3);
        }));

        var occurrence = Assert.Single(Import(payload).Occurrences);

        Assert.Null(occurrence.EnclosingRange);
        Assert.Equal(new SourceRange(0, 4, 0, 7), occurrence.Range);
    }

    [Fact]
    public void The_body_span_is_converted_through_the_same_encoding_as_the_name()
    {
        // UTF-32 columns over a line whose first character is outside the BMP. A body span
        // measured in one encoding beside a name measured in another would be worse than none.
        var payload = Index(index => index.Message(2, document =>
        {
            document.String(1, "src/demo.py");
            document.Message(2, occurrence =>
            {
                occurrence.PackedInt32(1, 0, 2, 5);
                occurrence.String(2, FunctionSymbol);
                occurrence.Int32(3, 1);
                occurrence.PackedInt32(7, 0, 0, 1, 5);
            });
            document.String(5, "🚀 foo\nfoo()");
            document.Int32(6, 3);
        }));

        var occurrence = Assert.Single(Import(payload).Occurrences);

        // The emoji is one UTF-32 character and two UTF-16 ones, so every column after it shifts
        // by one — in the body span exactly as in the name.
        Assert.Equal(new SourceRange(0, 3, 0, 6), occurrence.Range);
        Assert.Equal(new SourceRange(0, 0, 1, 5), occurrence.EnclosingRange);
    }

    private static SemanticIndex Import(byte[] payload) =>
        new ScipImporter().Supplement(
            EmptyIndex(), new MemoryStream(payload), Path.GetTempPath());

    private static byte[] Index(Action<ProtoBuilder> write)
    {
        var builder = new ProtoBuilder();
        write(builder);
        return builder.ToArray();
    }

    private static SemanticSnapshotIdentity Snapshot() =>
        new("repository", "generation", "tree", "dirty");

    private static SemanticIndex EmptyIndex() =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = "dirty",
            BaseCommit = "commit",
            IndexedAtUtc = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc),
            Documents = [],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    private sealed class ProtoBuilder
    {
        private readonly List<byte> _bytes = [];

        public void Int32(int field, int value)
        {
            Tag(field, 0);
            Varint((ulong)value);
        }

        public void String(int field, string value)
        {
            Tag(field, 2);
            Bytes(Encoding.UTF8.GetBytes(value));
        }

        public void Message(int field, Action<ProtoBuilder> write)
        {
            var nested = new ProtoBuilder();
            write(nested);
            Tag(field, 2);
            Bytes(nested.ToArray());
        }

        public void PackedInt32(int field, params int[] values)
        {
            var packed = new ProtoBuilder();
            foreach (var value in values)
            {
                packed.Varint((ulong)value);
            }

            Tag(field, 2);
            Bytes(packed.ToArray());
        }

        public byte[] ToArray() => [.. _bytes];

        private void Tag(int field, int wire) => Varint((ulong)((field << 3) | wire));

        private void Bytes(byte[] value)
        {
            Varint((ulong)value.Length);
            _bytes.AddRange(value);
        }

        private void Varint(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }

            _bytes.Add((byte)value);
        }
    }
}
