using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class ExperimentStateStore
{
    private const int SchemaVersion = 1;

    public ExperimentStateStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        StatePath = Path.Combine(
            Path.GetFullPath(runtimeRoot),
            "experiments",
            "state.json");
    }

    public string StatePath { get; }

    public async Task<ExperimentSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath))
        {
            return ExperimentSnapshot.Empty;
        }

        await using var stream = new FileStream(
            StatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<ExperimentStateDocument>(
            stream,
            LocalAiJson.Strict,
            cancellationToken)
            ?? throw new InvalidDataException("Experiment state is empty.");
        if (document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported experiment state schema '{document.SchemaVersion}'.");
        }

        return ExperimentSnapshot.FromPairs(document.Pairs);
    }

    public async Task SaveAsync(
        ExperimentSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".state-{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new ExperimentStateDocument(
                SchemaVersion,
                snapshot.Pairs
                    .OrderBy(pair => pair.Profile)
                    .ThenBy(pair => pair.Model, StringComparer.Ordinal)
                    .ToArray());
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    LocalAiJson.Strict,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ExperimentStateDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] IReadOnlyList<ExperimentPairState> Pairs);
}
