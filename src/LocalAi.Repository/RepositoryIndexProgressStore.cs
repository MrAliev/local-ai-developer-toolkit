using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Repository;

public sealed class RepositoryIndexProgressStore
{
    private readonly string _path;

    public RepositoryIndexProgressStore(FsPath repositoryRuntimeRoot)
    {
        _path = repositoryRuntimeRoot.Combine("progress.json").Value;
    }

    public void Save(RepositoryIndexProgress progress)
    {
        Validate(progress);
        Directory.CreateDirectory(
            Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Progress directory is unavailable."));
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    new ProgressDocument(1, progress),
                    LocalAiJson.Strict);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public RepositoryIndexProgress? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        ProgressDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ProgressDocument>(
                File.ReadAllText(_path),
                LocalAiJson.Strict)
                ?? throw new InvalidDataException("Repository progress is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Repository progress JSON is invalid.", exception);
        }
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException("Repository progress schema is unsupported.");
        }

        Validate(document.Progress);
        return document.Progress;
    }

    private static void Validate(RepositoryIndexProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (string.IsNullOrWhiteSpace(progress.RepositoryId) ||
            string.IsNullOrWhiteSpace(progress.WorkingRoot) ||
            progress.ProcessedChunks < 0 ||
            progress.TotalChunks < 0 ||
            progress.ProcessedChunks > progress.TotalChunks ||
            !double.IsFinite(progress.ChunksPerSecond) ||
            progress.ChunksPerSecond < 0 ||
            progress.EstimatedRemaining < TimeSpan.Zero ||
            progress.UpdatedAtUtc == default)
        {
            throw new InvalidDataException("Repository progress is invalid.");
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ProgressDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] RepositoryIndexProgress Progress);
}
