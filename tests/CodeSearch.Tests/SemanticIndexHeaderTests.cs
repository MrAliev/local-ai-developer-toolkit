using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// <see cref="SemanticIndex.TryReadDocumentCount"/> walks the header itself rather than loading
/// the index, because the status line that asks the question is printed after every query and a
/// real semantic index runs to tens of megabytes.
///
/// Walking it separately means two places now know the header layout, and the one that is not
/// exercised by every save is this one. These tests are what keeps them in step: they go through
/// the writer, so a field added to the header without being skipped here fails rather than
/// returning a number read out of the middle of a string.
/// </summary>
public sealed class SemanticIndexHeaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-sidx-header-" + Guid.NewGuid().ToString("N"));

    public SemanticIndexHeaderTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void The_document_count_matches_what_was_written(int documents)
    {
        var path = Path.Combine(_root, $"semantic-{documents}.sidx");
        Index(documents).Save(path);

        Assert.Equal(documents, SemanticIndex.TryReadDocumentCount(path));
    }

    /// <summary>
    /// The dirty hash is optional and sits in the middle of the header, so it is the field most
    /// able to knock the reader out of step by one string.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("a3f1")]
    public void The_optional_dirty_hash_does_not_shift_the_count(string? dirtyHash)
    {
        var path = Path.Combine(_root, $"semantic-dirty-{dirtyHash ?? "none"}.sidx");
        (Index(3) with { DirtyHash = dirtyHash }).Save(path);

        Assert.Equal(3, SemanticIndex.TryReadDocumentCount(path));
    }

    [Fact]
    public void A_file_that_is_not_a_semantic_index_reports_nothing()
    {
        var path = Path.Combine(_root, "not-an-index.sidx");
        File.WriteAllText(path, "this is not a semantic index");

        Assert.Null(SemanticIndex.TryReadDocumentCount(path));
    }

    [Fact]
    public void A_missing_file_reports_nothing()
    {
        Assert.Null(SemanticIndex.TryReadDocumentCount(Path.Combine(_root, "absent.sidx")));
    }

    private static SemanticIndex Index(int documents) =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            BaseCommit = "commit",
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = Enumerable
                .Range(0, documents)
                .Select(number => new SemanticDocument
                {
                    RelPath = $"src/File{number}.cs",
                    Hash = new byte[32],
                })
                .ToList(),
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
