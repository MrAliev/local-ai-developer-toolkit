using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.Repository.Tests;

public sealed class RepositoryIndexProgressStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-progress-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_is_atomic_and_round_trips_progress_and_eta()
    {
        var store = new RepositoryIndexProgressStore(FsPath.From(_root));
        var expected = new RepositoryIndexProgress(
            "repository",
            RepositoryIndexProgressPhase.EmbeddingBase,
            @"C:\repo",
            120,
            300,
            2.5,
            TimeSpan.FromSeconds(72),
            DateTimeOffset.UtcNow);

        store.Save(expected);

        var actual = Assert.IsType<RepositoryIndexProgress>(store.Read());
        Assert.Equal(expected, actual);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(11, 10)]
    public void Save_rejects_invalid_chunk_counters(int processed, int total)
    {
        var store = new RepositoryIndexProgressStore(FsPath.From(_root));
        var progress = new RepositoryIndexProgress(
            "repository",
            RepositoryIndexProgressPhase.EmbeddingBase,
            @"C:\repo",
            processed,
            total,
            1,
            TimeSpan.Zero,
            DateTimeOffset.UtcNow);

        Assert.Throws<InvalidDataException>(() => store.Save(progress));
    }

    [Fact]
    public void Read_reports_zero_filled_progress_as_invalid_data()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "progress.json"), new byte[337]);

        var error = Assert.Throws<InvalidDataException>(
            () => new RepositoryIndexProgressStore(FsPath.From(_root)).Read());

        Assert.IsType<System.Text.Json.JsonException>(error.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
