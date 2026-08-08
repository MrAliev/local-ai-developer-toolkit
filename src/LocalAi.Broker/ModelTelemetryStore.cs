using System.Collections;
using System.Reflection;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed record ModelTelemetryRecord(
    Guid JobId,
    LocalTaskProfile TaskProfile,
    string Model,
    int ContextTokens,
    LocalSizeBucket Input,
    LocalSizeBucket Output,
    bool WasCold,
    bool ModelSwitched,
    bool UsedFallback,
    string ValidatorResult,
    ModelExecutionOutcome Outcome,
    TimeSpan QueueDuration,
    TimeSpan LoadDuration,
    TimeSpan ExecutionDuration,
    TimeSpan TotalDuration,
    long EstimatedGrossCloudTokensSaved,
    long EstimatedVerificationTokens,
    long EstimatedNetCloudTokensSaved,
    string CatalogVersion,
    DateTimeOffset RecordedAtUtc);

public sealed record ExperimentTaskTelemetryRecord(
    Guid WorkflowId,
    LocalTaskProfile TaskProfile,
    string Model,
    ModelExecutionOutcome Outcome,
    TimeSpan TotalDuration,
    int ColdExecutions,
    int WarmExecutions,
    bool UsedFallback,
    int InputTokens,
    int OutputTokens,
    int LocalTokensProcessed,
    int EstimatedCloudGenerationTokensSaved,
    int EstimatedNetCloudContextTokensSaved,
    string CatalogVersion,
    DateTimeOffset RecordedAtUtc);

public sealed class ModelTelemetryStore
{
    private static readonly string[] ForbiddenMemberFragments =
        ["prompt", "answer", "content", "image", "path", "secret"];

    public ModelTelemetryStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        MetricsDirectory = Path.Combine(
            Path.GetFullPath(runtimeRoot),
            "telemetry",
            "metrics");
        ExperimentTasksDirectory = Path.Combine(
            Path.GetFullPath(runtimeRoot),
            "telemetry",
            "experiment-tasks");
    }

    public string MetricsDirectory { get; }

    public string ExperimentTasksDirectory { get; }

    public async Task AppendAsync(
        ModelTelemetryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureContentFree(record);
        Directory.CreateDirectory(MetricsDirectory);
        var destination = Path.Combine(
            MetricsDirectory,
            $"{record.RecordedAtUtc.UtcTicks:D19}-{record.JobId:N}-{Guid.NewGuid():N}.json");
        var temporary = destination + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    record,
                    LocalAiJson.Strict,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<IReadOnlyList<ModelTelemetryRecord>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(MetricsDirectory))
        {
            return [];
        }

        var records = new List<ModelTelemetryRecord>();
        foreach (var path in Directory.EnumerateFiles(MetricsDirectory, "*.json")
                     .Order(StringComparer.Ordinal))
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            records.Add(
                await JsonSerializer.DeserializeAsync<ModelTelemetryRecord>(
                    stream,
                    LocalAiJson.Strict,
                    cancellationToken)
                ?? throw new InvalidDataException("Telemetry record is empty."));
        }

        return records.AsReadOnly();
    }

    public async Task AppendExperimentTaskAsync(
        ExperimentTaskTelemetryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureContentFree(record);
        Directory.CreateDirectory(ExperimentTasksDirectory);
        var destination = Path.Combine(
            ExperimentTasksDirectory,
            $"{record.WorkflowId:N}.json");
        if (File.Exists(destination))
        {
            return;
        }

        // Trimming belongs to the operation that changes the history, not to the one that reads
        // it. Doing it here keeps the week-long bound this store has always had while leaving a
        // report free to be run as often as anyone likes.
        PruneExperimentTasks(record.RecordedAtUtc - ExperimentTaskRetention);

        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    record,
                    LocalAiJson.Strict,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// How long a completed experiment task keeps its measurements. The experiment state keeps
    /// the attempt and outcome counts for as long as the experiment runs; this bounds only the
    /// per-task timings and token figures.
    /// </summary>
    public static TimeSpan ExperimentTaskRetention { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Deletes experiment task records recorded before <paramref name="cutoff"/>.
    ///
    /// Failures are swallowed: this is housekeeping attached to a write, and a file that cannot
    /// be removed right now must not fail the record that triggered the attempt.
    /// </summary>
    public void PruneExperimentTasks(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(ExperimentTasksDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(ExperimentTasksDirectory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ExperimentTaskTelemetryRecord>(
                    File.ReadAllText(path),
                    LocalAiJson.Strict);
                if (record is not null && record.RecordedAtUtc < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException or
                    InvalidDataException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the experiment task records, and only reads them.
    ///
    /// This used to delete anything older than seven days on its way through, which made the
    /// experiment report destructive: looking at it discarded the history it was reporting on,
    /// and the more often it was consulted the less there was left to consult. A pair with six
    /// completed attempts answered with one, because the other five files had been deleted by
    /// earlier reads.
    ///
    /// Retention is a policy decision that belongs to a prune, not a side effect of a getter.
    /// </summary>
    public async Task<IReadOnlyList<ExperimentTaskTelemetryRecord>>
        ReadExperimentTasksAsync(
            CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ExperimentTasksDirectory))
        {
            return [];
        }

        var records = new List<ExperimentTaskTelemetryRecord>();
        foreach (var path in Directory
                     .EnumerateFiles(ExperimentTasksDirectory, "*.json")
                     .Order(StringComparer.Ordinal))
        {
            ExperimentTaskTelemetryRecord record;
            await using (var stream = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                record = await JsonSerializer
                    .DeserializeAsync<ExperimentTaskTelemetryRecord>(
                        stream,
                        LocalAiJson.Strict,
                        cancellationToken)
                ?? throw new InvalidDataException(
                    "Experiment task telemetry record is empty.");
            }

            records.Add(record);
        }

        return records.AsReadOnly();
    }

    public static void EnsureContentFree(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Inspect(value, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static void Inspect(object? value, HashSet<object> visited)
    {
        if (value is null ||
            value is string ||
            value.GetType().IsPrimitive ||
            value is decimal or Guid or DateTimeOffset or TimeSpan ||
            value.GetType().IsEnum)
        {
            return;
        }

        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            return;
        }

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                RejectName(entry.Key?.ToString());
                Inspect(entry.Value, visited);
            }

            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                Inspect(item, visited);
            }

            return;
        }

        foreach (var property in value.GetType().GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            RejectName(property.Name);
            Inspect(property.GetValue(value), visited);
        }
    }

    private static void RejectName(string? name)
    {
        if (name is not null &&
            ForbiddenMemberFragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Telemetry member '{name}' is not content-free.");
        }
    }
}
