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

    public async Task<IReadOnlyList<ExperimentTaskTelemetryRecord>>
        ReadExperimentTasksAsync(
            CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ExperimentTasksDirectory))
        {
            return [];
        }

        var records = new List<ExperimentTaskTelemetryRecord>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
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

            if (record.RecordedAtUtc < cutoff)
            {
                File.Delete(path);
                continue;
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
