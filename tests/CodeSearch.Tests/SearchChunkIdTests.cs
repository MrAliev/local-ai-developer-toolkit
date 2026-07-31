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
    public void Round_trips_a_non_ascii_payload_at_the_serialized_limit()
    {
        var field = new string('\u00e9', 256);
        var expected = new SearchChunkId(
            field,
            field,
            field,
            new string('\u00e9', 249),
            42);

        var encoded = expected.Encode();

        Assert.Equal(2779, encoded.Length);
        Assert.Equal(expected, SearchChunkId.Parse(encoded));
    }

    [Fact]
    public void Encode_rejects_a_serialized_payload_beyond_the_parse_limit()
    {
        var field = new string('\u00e9', 256);
        var oversized = new SearchChunkId(
            field,
            field,
            field,
            new string('\u00e9', 250),
            42);

        var error = Assert.Throws<ArgumentException>(() => oversized.Encode());

        Assert.Contains("payload", error.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Rejects_oversized_ids_before_allocating_segments()
    {
        var oversized = "cs1." + new string('A', 1_000_000);
        _ = Assert.Throws<SearchChunkIdException>(
            () => SearchChunkId.Parse("invalid"));
        var before = GC.GetAllocatedBytesForCurrentThread();

        var error = Assert.Throws<SearchChunkIdException>(
            () => SearchChunkId.Parse(oversized));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("chunk_id_malformed", error.Code);
        Assert.True(
            allocated < oversized.Length,
            $"Oversized parsing allocated {allocated} bytes.");
    }

    [Fact]
    public void Rejects_an_oversized_payload_segment()
    {
        var oversizedPayload = new string('A', 2732);
        var shortDigest = new string('A', 42);

        var error = Assert.Throws<SearchChunkIdException>(
            () => SearchChunkId.Parse($"cs1.{oversizedPayload}.{shortDigest}"));

        Assert.Equal("chunk_id_malformed", error.Code);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(44)]
    public void Rejects_digest_segments_that_are_not_the_exact_length(int length)
    {
        var error = Assert.Throws<SearchChunkIdException>(
            () => SearchChunkId.Parse($"cs1.A.{new string('A', length)}"));

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
