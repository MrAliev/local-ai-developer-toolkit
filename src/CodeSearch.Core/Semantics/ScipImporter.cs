using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Indexing;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Core.Semantics;

public sealed record ScipImportLimits(
    int MaximumBytes = 256 * 1024 * 1024,
    int MaximumDocuments = 1_000_000,
    int MaximumOccurrences = 10_000_000,
    int MaximumSymbols = 5_000_000,
    int MaximumStringBytes = 4 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaximumBytes <= 0 || MaximumDocuments <= 0 || MaximumOccurrences <= 0 ||
            MaximumSymbols <= 0 || MaximumStringBytes <= 0 ||
            MaximumStringBytes > MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ScipImportLimits));
        }
    }
}

/// <summary>
/// Imports binary SCIP v0.5.1 indexes without materializing the generated Protobuf object graph.
/// Unknown fields are skipped, while sizes, counts, paths, encodings, and ranges fail closed.
/// </summary>
public sealed class ScipImporter
{
    public const string SchemaVersion = "v0.5.1";
    public const string SchemaUrl = "https://github.com/sourcegraph/scip/blob/v0.5.1/scip.proto";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ScipImportLimits _limits;

    public ScipImporter(ScipImportLimits? limits = null)
    {
        _limits = limits ?? new ScipImportLimits();
        _limits.Validate();
    }

    public SemanticIndex Supplement(
        SemanticIndex baseIndex,
        Stream input,
        string repositoryRoot,
        ScipPositionEncoding? unspecifiedPositionEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(baseIndex);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (unspecifiedPositionEncoding is not null &&
            !Enum.IsDefined(unspecifiedPositionEncoding.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(unspecifiedPositionEncoding));
        }
        var payload = ReadBounded(input);
        var parsed = ParseIndex(new ProtoReader(payload, _limits));
        var root = Path.GetFullPath(repositoryRoot);
        var documents = baseIndex.Documents.ToDictionary(
            document => document.RelPath,
            StringComparer.Ordinal);
        var symbols = baseIndex.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        var occurrences = baseIndex.Occurrences.ToList();
        var relationships = baseIndex.Relationships.ToHashSet();

        foreach (var external in parsed.ExternalSymbols)
        {
            AddSymbol(symbols, external, documentPath: null);
        }

        foreach (var document in parsed.Documents)
        {
            var relativePath = NormalizePath(document.RelativePath);
            var text = ReadDocumentText(root, relativePath, document.Text);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            if (documents.TryGetValue(relativePath, out var existing))
            {
                if (!existing.Hash.AsSpan().SequenceEqual(hash))
                {
                    throw new InvalidDataException(
                        $"SCIP document '{relativePath}' conflicts with the current semantic snapshot.");
                }
            }
            else
            {
                documents.Add(relativePath, new SemanticDocument
                {
                    RelPath = relativePath,
                    Hash = hash,
                });
            }

            foreach (var information in document.Symbols)
            {
                AddSymbol(symbols, information, relativePath);
            }

            foreach (var information in document.Symbols)
            {
                var sourceId = SymbolId(information.Symbol, relativePath);
                foreach (var relationship in information.Relationships)
                {
                    var targetId = SymbolId(relationship.Symbol, relativePath);
                    EnsureUnknownSymbol(symbols, targetId);
                    var kind = relationship.IsImplementation
                        ? SemanticRelationshipKind.Implementation
                        : relationship.IsTypeDefinition || relationship.IsDefinition
                            ? SemanticRelationshipKind.TypeDefinition
                            : SemanticRelationshipKind.Unknown;
                    if (kind != SemanticRelationshipKind.Unknown)
                    {
                        relationships.Add(new SemanticRelationship
                        {
                            SourceSymbolId = sourceId,
                            TargetSymbolId = targetId,
                            Kind = kind,
                        });
                    }
                }
            }

            var sourceText = SourceText.From(text);
            foreach (var occurrence in document.Occurrences)
            {
                if (string.IsNullOrWhiteSpace(occurrence.Symbol))
                {
                    continue;
                }

                var symbolId = SymbolId(occurrence.Symbol, relativePath);
                EnsureUnknownSymbol(symbols, symbolId);
                occurrences.Add(new SemanticOccurrence
                {
                    DocumentPath = relativePath,
                    Range = ConvertRange(
                        occurrence.Range,
                        document.PositionEncoding == 0 && unspecifiedPositionEncoding is not null
                            ? (int)unspecifiedPositionEncoding.Value
                            : document.PositionEncoding,
                        sourceText,
                        relativePath),
                    SymbolId = symbolId,
                    Roles = ConvertRoles(occurrence.SymbolRoles),
                    Precision = NavigationPrecision.Precise,
                });
            }
        }

        return baseIndex with
        {
            Documents = documents.Values.ToList(),
            Symbols = symbols.Values.ToList(),
            Occurrences = occurrences,
            Relationships = relationships.ToList(),
        };
    }

    private ParsedIndex ParseIndex(ProtoReader reader)
    {
        var budget = new ParseBudget(_limits);
        var documents = new List<ScipDocument>();
        var external = new List<ScipSymbolInformation>();
        while (reader.TryReadField(out var field, out var wire))
        {
            switch (field)
            {
                case 2 when wire == 2:
                    budget.AddDocument();
                    documents.Add(ParseDocument(reader.ReadMessage(), budget));
                    break;
                case 3 when wire == 2:
                    budget.AddSymbol();
                    external.Add(ParseSymbolInformation(reader.ReadMessage()));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new ParsedIndex(documents, external);
    }

    private ScipDocument ParseDocument(ProtoReader reader, ParseBudget budget)
    {
        var path = string.Empty;
        var text = string.Empty;
        var encoding = 0;
        var occurrences = new List<ScipOccurrence>();
        var symbols = new List<ScipSymbolInformation>();
        while (reader.TryReadField(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    path = reader.ReadString();
                    break;
                case 2 when wire == 2:
                    budget.AddOccurrence();
                    occurrences.Add(ParseOccurrence(reader.ReadMessage()));
                    break;
                case 3 when wire == 2:
                    budget.AddSymbol();
                    symbols.Add(ParseSymbolInformation(reader.ReadMessage()));
                    break;
                case 5 when wire == 2:
                    text = reader.ReadString();
                    break;
                case 6 when wire == 0:
                    encoding = reader.ReadInt32();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new ScipDocument(path, text, encoding, occurrences, symbols);
    }

    private ScipOccurrence ParseOccurrence(ProtoReader reader)
    {
        var range = new List<int>(4);
        var symbol = string.Empty;
        var roles = 0;
        while (reader.TryReadField(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                {
                    var packed = reader.ReadMessage();
                    while (!packed.End)
                    {
                        range.Add(packed.ReadInt32());
                    }

                    break;
                }
                case 1 when wire == 0:
                    range.Add(reader.ReadInt32());
                    break;
                case 2 when wire == 2:
                    symbol = reader.ReadString();
                    break;
                case 3 when wire == 0:
                    roles = reader.ReadInt32();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (range.Count is not (3 or 4))
        {
            throw new InvalidDataException("SCIP occurrence range must contain three or four integers.");
        }

        return new ScipOccurrence(range.ToArray(), symbol, roles);
    }

    private ScipSymbolInformation ParseSymbolInformation(ProtoReader reader)
    {
        var symbol = string.Empty;
        var displayName = string.Empty;
        var documentation = new List<string>();
        var relationships = new List<ScipRelationship>();
        var kind = 0;
        while (reader.TryReadField(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    symbol = reader.ReadString();
                    break;
                case 3 when wire == 2:
                    documentation.Add(reader.ReadString());
                    break;
                case 4 when wire == 2:
                    relationships.Add(ParseRelationship(reader.ReadMessage()));
                    break;
                case 5 when wire == 0:
                    kind = reader.ReadInt32();
                    break;
                case 6 when wire == 2:
                    displayName = reader.ReadString();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new InvalidDataException("SCIP SymbolInformation.symbol is required.");
        }

        return new ScipSymbolInformation(
            symbol,
            displayName,
            string.Join("\n\n", documentation),
            kind,
            relationships);
    }

    private static ScipRelationship ParseRelationship(ProtoReader reader)
    {
        var symbol = string.Empty;
        var implementation = false;
        var typeDefinition = false;
        var definition = false;
        while (reader.TryReadField(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    symbol = reader.ReadString();
                    break;
                case 3 when wire == 0:
                    implementation = reader.ReadBool();
                    break;
                case 4 when wire == 0:
                    typeDefinition = reader.ReadBool();
                    break;
                case 5 when wire == 0:
                    definition = reader.ReadBool();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new InvalidDataException("SCIP relationship symbol is required.");
        }

        return new ScipRelationship(symbol, implementation, typeDefinition, definition);
    }

    private byte[] ReadBounded(Stream input)
    {
        if (input.CanSeek && input.Length - input.Position > _limits.MaximumBytes)
        {
            throw new InvalidDataException($"SCIP payload exceeds {_limits.MaximumBytes} bytes.");
        }

        using var output = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var total = 0;
            int read;
            while ((read = input.Read(rented, 0, rented.Length)) > 0)
            {
                total = checked(total + read);
                if (total > _limits.MaximumBytes)
                {
                    throw new InvalidDataException($"SCIP payload exceeds {_limits.MaximumBytes} bytes.");
                }

                output.Write(rented, 0, read);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void AddSymbol(
        Dictionary<string, SemanticSymbol> symbols,
        ScipSymbolInformation information,
        string? documentPath)
    {
        var id = SymbolId(information.Symbol, documentPath);
        symbols.TryAdd(id, new SemanticSymbol
        {
            Id = id,
            DisplayName = string.IsNullOrWhiteSpace(information.DisplayName)
                ? InferDisplayName(information.Symbol)
                : information.DisplayName,
            Kind = ConvertKind(information.Kind),
            Documentation = string.IsNullOrWhiteSpace(information.Documentation)
                ? null
                : information.Documentation,
        });
    }

    private static void EnsureUnknownSymbol(
        Dictionary<string, SemanticSymbol> symbols,
        string id) =>
        symbols.TryAdd(id, new SemanticSymbol
        {
            Id = id,
            DisplayName = InferDisplayName(id),
            Kind = SemanticSymbolKind.Unknown,
        });

    private static SourceRange ConvertRange(
        int[] range,
        int encoding,
        SourceText text,
        string path)
    {
        var startLine = range[0];
        var endLine = range.Length == 3 ? startLine : range[2];
        var startCharacter = range[1];
        var endCharacter = range.Length == 3 ? range[2] : range[3];
        if (startLine < 0 || endLine < startLine || endLine >= text.Lines.Count ||
            startCharacter < 0 || endCharacter < 0)
        {
            throw new InvalidDataException($"SCIP occurrence range is outside '{path}'.");
        }

        var convertedStart = ToUtf16(text.Lines[startLine].ToString(), startCharacter, encoding);
        var convertedEnd = ToUtf16(text.Lines[endLine].ToString(), endCharacter, encoding);
        var result = new SourceRange(startLine, convertedStart, endLine, convertedEnd);
        if (endLine == startLine && convertedEnd < convertedStart)
        {
            throw new InvalidDataException($"SCIP occurrence range is reversed in '{path}'.");
        }

        return result;
    }

    private static int ToUtf16(string line, int offset, int encoding)
    {
        if (encoding == 2)
        {
            if (offset > line.Length)
            {
                throw new InvalidDataException("SCIP UTF-16 position exceeds the source line.");
            }

            return offset;
        }

        if (encoding is not (1 or 3))
        {
            throw new InvalidDataException("SCIP document position_encoding must be explicit.");
        }

        var units = 0;
        var utf16 = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (units == offset)
            {
                return utf16;
            }

            var next = units + (encoding == 1 ? rune.Utf8SequenceLength : 1);
            if (next > offset)
            {
                throw new InvalidDataException("SCIP position splits an encoded character.");
            }

            units = next;
            utf16 += rune.Utf16SequenceLength;
        }

        return units == offset
            ? utf16
            : throw new InvalidDataException("SCIP position exceeds the source line.");
    }

    private static SemanticOccurrenceRoles ConvertRoles(int roles)
    {
        var result = (roles & 0x1) != 0
            ? SemanticOccurrenceRoles.Definition
            : SemanticOccurrenceRoles.Reference;
        if ((roles & 0x2) != 0) result |= SemanticOccurrenceRoles.Import;
        if ((roles & 0x4) != 0) result |= SemanticOccurrenceRoles.Write;
        if ((roles & 0x8) != 0) result |= SemanticOccurrenceRoles.Read;
        return result;
    }

    private static SemanticSymbolKind ConvertKind(int kind) => kind switch
    {
        7 or 11 or 21 or 49 or 54 or 55 or 59 => SemanticSymbolKind.Type,
        9 or 17 or 26 or 66 or 67 or 68 or 69 or 70 or 71 or 76 or 80 =>
            SemanticSymbolKind.Method,
        13 or 78 => SemanticSymbolKind.Event,
        15 or 77 or 79 => SemanticSymbolKind.Field,
        18 or 41 or 45 or 47 or 81 => SemanticSymbolKind.Property,
        29 or 30 or 35 => SemanticSymbolKind.Namespace,
        37 or 38 or 44 or 52 => SemanticSymbolKind.Parameter,
        61 or 82 => SemanticSymbolKind.Local,
        _ => SemanticSymbolKind.Unknown,
    };

    private static string SymbolId(string symbol, string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new InvalidDataException("SCIP occurrence symbol is empty.");
        }

        if (!symbol.StartsWith("local ", StringComparison.Ordinal))
        {
            ValidateGlobalSymbol(symbol);
            return symbol;
        }

        var localId = symbol[6..];
        if (localId.Length == 0 || localId.Any(character =>
                !(character is '_' or '+' or '-' or '$' ||
                  character is >= 'a' and <= 'z' ||
                  character is >= 'A' and <= 'Z' ||
                  character is >= '0' and <= '9')))
        {
            throw new InvalidDataException("SCIP local symbol ID is invalid.");
        }

        if (documentPath is null)
        {
            throw new InvalidDataException("SCIP local symbol has no containing document.");
        }

        return $"scip-local {documentPath} {localId}";
    }

    private static void ValidateGlobalSymbol(string symbol)
    {
        var offset = 0;
        var scheme = ReadSymbolComponent(symbol, ref offset);
        _ = ReadSymbolComponent(symbol, ref offset); // package manager
        _ = ReadSymbolComponent(symbol, ref offset); // package name
        _ = ReadSymbolComponent(symbol, ref offset); // package version
        if (scheme.StartsWith("local", StringComparison.Ordinal) ||
            offset >= symbol.Length ||
            string.IsNullOrWhiteSpace(symbol[offset..]))
        {
            throw new InvalidDataException("SCIP global symbol identity is invalid.");
        }
    }

    private static string ReadSymbolComponent(string symbol, ref int offset)
    {
        var value = new StringBuilder();
        while (offset < symbol.Length)
        {
            var current = symbol[offset++];
            if (current != ' ')
            {
                value.Append(current);
                continue;
            }

            if (offset < symbol.Length && symbol[offset] == ' ')
            {
                value.Append(' ');
                offset++;
                continue;
            }

            if (value.Length == 0)
            {
                break;
            }

            return value.ToString();
        }

        throw new InvalidDataException("SCIP symbol package identity is incomplete.");
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"SCIP document path '{path}' is not canonical.");
        }

        return normalized;
    }

    private static string ReadDocumentText(string root, string path, string embedded)
    {
        if (embedded.Length > 0)
        {
            return embedded;
        }

        if (!SafeSourcePath.TryResolveFile(root, path, out var fullPath, out var failure))
        {
            throw new InvalidDataException(
                $"SCIP document '{path}' cannot be read safely ({failure}).");
        }

        return File.ReadAllText(fullPath);
    }

    private static string InferDisplayName(string symbol)
    {
        var value = symbol.Trim();
        var separator = value.LastIndexOfAny(['/', '#', '.', ' ']);
        var name = separator >= 0 && separator + 1 < value.Length
            ? value[(separator + 1)..]
            : value;
        return name.TrimEnd('.', '#', '/', ':', '!', ')', '(') is { Length: > 0 } result
            ? result
            : "symbol";
    }

    private static void RequireCount(int current, int maximum, string kind)
    {
        if (current >= maximum)
        {
            throw new InvalidDataException($"SCIP {kind} count exceeds {maximum}.");
        }
    }

    private sealed class ParseBudget(ScipImportLimits limits)
    {
        private int _documents;
        private int _occurrences;
        private int _symbols;

        public void AddDocument() =>
            Consume(ref _documents, limits.MaximumDocuments, "document");

        public void AddOccurrence() =>
            Consume(ref _occurrences, limits.MaximumOccurrences, "occurrence");

        public void AddSymbol() =>
            Consume(ref _symbols, limits.MaximumSymbols, "symbol");

        private static void Consume(ref int count, int maximum, string kind)
        {
            RequireCount(count, maximum, kind);
            count++;
        }
    }

    private sealed record ParsedIndex(
        List<ScipDocument> Documents,
        List<ScipSymbolInformation> ExternalSymbols);
    private sealed record ScipDocument(
        string RelativePath,
        string Text,
        int PositionEncoding,
        List<ScipOccurrence> Occurrences,
        List<ScipSymbolInformation> Symbols);
    private sealed record ScipOccurrence(int[] Range, string Symbol, int SymbolRoles);
    private sealed record ScipSymbolInformation(
        string Symbol,
        string DisplayName,
        string Documentation,
        int Kind,
        List<ScipRelationship> Relationships);
    private sealed record ScipRelationship(
        string Symbol,
        bool IsImplementation,
        bool IsTypeDefinition,
        bool IsDefinition);

    private sealed class ProtoReader
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly ScipImportLimits _limits;
        private int _offset;

        public ProtoReader(ReadOnlyMemory<byte> data, ScipImportLimits limits)
        {
            _data = data;
            _limits = limits;
        }

        public bool End => _offset == _data.Length;

        public bool TryReadField(out int field, out int wire)
        {
            if (End)
            {
                field = wire = 0;
                return false;
            }

            var tag = ReadVarint();
            field = checked((int)(tag >> 3));
            wire = (int)(tag & 7);
            if (field <= 0 || wire is 3 or 4 or > 5)
            {
                throw new InvalidDataException("SCIP contains an invalid Protobuf tag.");
            }

            return true;
        }

        public int ReadInt32() => checked((int)ReadVarint());
        public bool ReadBool() => ReadVarint() != 0;

        public string ReadString()
        {
            var bytes = ReadBytes(_limits.MaximumStringBytes, "string");
            try
            {
                return StrictUtf8.GetString(bytes.Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("SCIP contains invalid UTF-8.", exception);
            }
        }

        public ProtoReader ReadMessage() => new(ReadBytes(_limits.MaximumBytes, "message"), _limits);

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: ReadVarint(); break;
                case 1: Advance(8); break;
                case 2: _ = ReadBytes(_limits.MaximumBytes, "length-delimited field"); break;
                case 5: Advance(4); break;
                default: throw new InvalidDataException("Unsupported Protobuf wire type.");
            }
        }

        private ReadOnlyMemory<byte> ReadBytes(int maximumBytes, string fieldKind)
        {
            var lengthValue = ReadVarint();
            if (lengthValue > (ulong)maximumBytes)
            {
                throw new InvalidDataException(
                    $"SCIP {fieldKind} exceeds {maximumBytes} bytes.");
            }

            var length = checked((int)lengthValue);
            if (length > _data.Length - _offset)
            {
                throw new EndOfStreamException("SCIP length-delimited field is truncated.");
            }

            var result = _data.Slice(_offset, length);
            _offset += length;
            return result;
        }

        private ulong ReadVarint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (_offset >= _data.Length)
                {
                    throw new EndOfStreamException("SCIP Protobuf varint is truncated.");
                }

                var current = _data.Span[_offset++];
                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new InvalidDataException("SCIP Protobuf varint is too long.");
        }

        private void Advance(int count)
        {
            if (count > _data.Length - _offset)
            {
                throw new EndOfStreamException("SCIP Protobuf field is truncated.");
            }

            _offset += count;
        }
    }
}
