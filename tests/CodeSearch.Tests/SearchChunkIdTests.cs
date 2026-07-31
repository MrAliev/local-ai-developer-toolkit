using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;

namespace CodeSearch.Tests;

public sealed class SearchChunkIdTests
{
    private static readonly SearchChunkId Expected = new(
        "repository",
        "generation",
        "tree",
        "dirty",
        42);

    [Fact]
    public void Round_trips_exact_snapshot_identity_and_ordinal()
    {
        var encoded = Expected.Encode();

        Assert.StartsWith("cs1.", encoded, StringComparison.Ordinal);
        Assert.Equal(Expected, SearchChunkId.Parse(encoded));
    }

    [Fact]
    public void Rejects_a_tampered_payload()
    {
        var encoded = Expected.Encode();
        var replacement = encoded[5] == 'A' ? 'B' : 'A';
        var tampered = encoded[..5] + replacement + encoded[6..];

        var error = Assert.Throws<SearchChunkIdException>(
            () => SearchChunkId.Parse(tampered));

        Assert.Equal("chunk_id_tampered", error.Code);
    }

    [Fact]
    public void Rejects_a_non_canonical_digest_with_equivalent_trailing_pad_bits()
    {
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var parts = Expected.Encode().Split('.');
        var finalIndex = alphabet.IndexOf(parts[2][^1]);
        Assert.Equal(0, finalIndex & 0x03);
        parts[2] = parts[2][..^1] + alphabet[finalIndex + 1];
        var nonCanonical = string.Join('.', parts);

        var error = Assert.Throws<SearchChunkIdException>(
            () => SearchChunkId.Parse(nonCanonical));

        Assert.Equal("chunk_id_malformed", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-chunk-id")]
    [InlineData("cs1.payload")]
    [InlineData("cs2.payload.digest")]
    [InlineData("cs1.***.***")]
    public void Rejects_malformed_ids(string value)
    {
        var error = Assert.Throws<SearchChunkIdException>(
            () => SearchChunkId.Parse(value));

        Assert.Equal("chunk_id_malformed", error.Code);
    }

    [Fact]
    public void Rejects_invalid_fields()
    {
        Assert.Throws<ArgumentException>(
            () => new SearchChunkId("", "generation", "tree", null, 0).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SearchChunkId("repository", "generation", "tree", null, -1).Encode());
    }

    [Theory]
    [InlineData("other", "generation", "tree", "dirty", "wrong_repository")]
    [InlineData("repository", "other", "tree", "dirty", "stale_generation")]
    [InlineData("repository", "generation", "other", "dirty", "stale_worktree")]
    [InlineData("repository", "generation", "tree", "other", "stale_overlay")]
    public void Snapshot_validation_reports_the_specific_mismatch(
        string repository,
        string generation,
        string tree,
        string? dirty,
        string expectedCode)
    {
        var actual = new SearchChunkId(repository, generation, tree, dirty, 42);

        var error = Assert.Throws<SearchChunkResolutionException>(
            () => SearchChunkResolver.ValidateSnapshot(Expected, actual));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void Snapshot_validation_accepts_an_exact_match()
    {
        SearchChunkResolver.ValidateSnapshot(Expected, Expected);
    }

    [Fact]
    public void Ordinal_validation_reports_out_of_range()
    {
        var error = Assert.Throws<SearchChunkResolutionException>(
            () => SearchChunkResolver.ValidateOrdinal(Expected with { Ordinal = 3 }, 3));

        Assert.Equal("chunk_out_of_range", error.Code);
    }
}
