using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Transactions;

public static class InstallerJournalSchema
{
    public const int CurrentVersion = 1;
}

public enum InstallerEffectKind
{
    DependencyInstall,
    PackageActivation,
    ModelInstall,
    AgentConfiguration,
}

public enum InstallerStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    RolledBack,
    RollbackFailed,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallerStepDefinition(
    string StepId,
    InstallerEffectKind EffectKind,
    bool IsTransactional)
{
    public static InstallerStepDefinition Transactional(
        string stepId,
        InstallerEffectKind effectKind) =>
        new(stepId, effectKind, true);

    public static InstallerStepDefinition NonTransactional(
        string stepId,
        InstallerEffectKind effectKind) =>
        new(stepId, effectKind, false);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallerStepResult(
    IReadOnlyDictionary<string, string> Hashes,
    IReadOnlyList<string> BackupPaths)
{
    public static InstallerStepResult Completed(
        string? artifactSha256,
        string? backupPath)
    {
        var hashes = artifactSha256 is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["artifact"] = artifactSha256,
            };
        var backups = backupPath is null ? [] : new[] { backupPath };
        return new(
            new ReadOnlyDictionary<string, string>(hashes),
            backups);
    }
}

public sealed record JournalNonTransactionalEffect(string StepId, string Description);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NonTransactionalJournalEffect(
    string StepId,
    InstallerEffectKind EffectKind,
    string Description);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallerJournalStep(
    string StepId,
    InstallerEffectKind EffectKind,
    bool IsTransactional,
    InstallerStepStatus Status,
    int Attempts,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyDictionary<string, string> Hashes,
    IReadOnlyList<string> BackupPaths,
    string? FailureCode,
    string? FailureMessage)
{
    public string? ArtifactSha256 => Hashes.TryGetValue("artifact", out var value) ? value : null;

    public string? BackupPath => BackupPaths.Count == 0 ? null : BackupPaths[0];

    public static InstallerJournalStep Pending(string stepId, bool transactional) =>
        Create(stepId, transactional, InstallerStepStatus.Pending, null, null, null);

    public static InstallerJournalStep Completed(
        string stepId,
        bool transactional,
        string? artifactSha256,
        string? backupPath = null) =>
        Create(stepId, transactional, InstallerStepStatus.Completed, artifactSha256, backupPath, null);

    public static InstallerJournalStep Failed(
        string stepId,
        bool transactional,
        string failureMessage) =>
        Create(stepId, transactional, InstallerStepStatus.Failed, null, null, failureMessage);

    private static InstallerJournalStep Create(
        string stepId,
        bool transactional,
        InstallerStepStatus status,
        string? artifactSha256,
        string? backupPath,
        string? failureMessage) =>
        new(
            stepId,
            transactional ? InstallerEffectKind.AgentConfiguration : InstallerEffectKind.DependencyInstall,
            transactional,
            status,
            status == InstallerStepStatus.Failed ? 1 : 0,
            null,
            null,
            artifactSha256 is null
                ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>())
                : new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["artifact"] = artifactSha256,
                    }),
            backupPath is null ? [] : [backupPath],
            status == InstallerStepStatus.Failed ? "failed" : null,
            failureMessage);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallerJournalSnapshot(
    int SchemaVersion,
    Guid TransactionId,
    Guid PlanId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<InstallerJournalStep> Steps,
    IReadOnlyList<NonTransactionalJournalEffect> NonTransactionalEffects)
{
    public static InstallerJournalSnapshot Start(
        Guid planId,
        IReadOnlyList<InstallerJournalStep> steps,
        IReadOnlyList<JournalNonTransactionalEffect> nonTransactionalEffects)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            InstallerJournalSchema.CurrentVersion,
            Guid.NewGuid(),
            planId,
            now,
            now,
            steps.ToArray(),
            nonTransactionalEffects
                .Select(effect => new NonTransactionalJournalEffect(
                    effect.StepId,
                    InstallerEffectKind.DependencyInstall,
                    effect.Description))
                .ToArray());
    }
}

public sealed class InstallerJournal
{
    private readonly object sync = new();
    private readonly TimeProvider? timeProvider;

    private InstallerJournal(string journalPath, InstallerJournalSnapshot snapshot)
    {
        JournalPath = journalPath;
        Snapshot = snapshot;
    }

    public InstallerJournal(string localAppData, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        JournalPath = System.IO.Path.Combine(localAppData, "LocalAi", "installer", "journal.json");
        var now = timeProvider.GetUtcNow();
        Snapshot = new(
            InstallerJournalSchema.CurrentVersion,
            Guid.NewGuid(),
            Guid.Empty,
            now,
            now,
            [],
            []);
    }

    public string JournalPath { get; }

    public string Path => JournalPath;

    public InstallerJournalSnapshot Snapshot { get; internal set; }

    public async Task SaveAsync(
        InstallerJournalSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Snapshot = snapshot with
        {
            UpdatedAtUtc = timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow,
        };
        Save();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<InstallerJournalSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var loaded = Load(JournalPath);
            Snapshot = loaded.Snapshot;
            return await Task.FromResult(Snapshot).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid installer journal.", ex);
        }
    }

    public static InstallerJournal Create(
        string localAppData,
        Guid transactionId,
        Guid planId,
        IReadOnlyList<InstallerStepDefinition> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentNullException.ThrowIfNull(steps);
        ValidateUniqueSteps(steps);
        var installerRoot = System.IO.Path.Combine(localAppData, "LocalAi", "installer");
        var path = System.IO.Path.Combine(installerRoot, transactionId + ".json");
        if (File.Exists(path))
        {
            return Load(path);
        }

        var now = DateTimeOffset.UtcNow;
        var snapshot = new InstallerJournalSnapshot(
            InstallerJournalSchema.CurrentVersion,
            transactionId,
            planId,
            now,
            now,
            steps.Select(step => new InstallerJournalStep(
                    step.StepId,
                    step.EffectKind,
                    step.IsTransactional,
                    InstallerStepStatus.Pending,
                    0,
                    null,
                    null,
                    new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()),
                    Array.Empty<string>(),
                    null,
                    null))
                .ToArray(),
            steps.Where(step => !step.IsTransactional)
                .Select(step => new NonTransactionalJournalEffect(
                    step.StepId,
                    step.EffectKind,
                    step.EffectKind switch
                    {
                        InstallerEffectKind.DependencyInstall => "Dependency installation may remain present after rollback.",
                        InstallerEffectKind.ModelInstall => "Model installation may remain present after rollback.",
                        _ => "Non-transactional installer effect may require manual follow-up.",
                    }))
                .ToArray());
        var journal = new InstallerJournal(path, snapshot);
        journal.Save();
        return journal;
    }

    public static InstallerJournal Load(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        try
        {
            var json = File.ReadAllText(journalPath);
            var snapshot = JsonSerializer.Deserialize<InstallerJournalSnapshot>(json, LocalAiJson.Strict)
                ?? throw new JsonException("Installer journal was empty.");
            if (snapshot.SchemaVersion != InstallerJournalSchema.CurrentVersion)
            {
                throw new JsonException("Unsupported installer journal schema version.");
            }

            ValidateUniqueSteps(snapshot.Steps.Select(step => new InstallerStepDefinition(
                    step.StepId,
                    step.EffectKind,
                    step.IsTransactional))
                .ToArray());
            return new InstallerJournal(journalPath, snapshot);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid installer journal.", ex);
        }
    }

    public Task RecordRunningAsync(string stepId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Transition(
            stepId,
            step =>
            {
                var now = DateTimeOffset.UtcNow;
                return step with
                {
                    Status = InstallerStepStatus.Running,
                    Attempts = step.Attempts + 1,
                    StartedAtUtc = step.StartedAtUtc ?? now,
                    UpdatedAtUtc = now,
                    FailureCode = null,
                    FailureMessage = null,
                };
            });
        return Task.CompletedTask;
    }

    public Task RecordCompletedAsync(
        string stepId,
        InstallerStepResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(result);
        Transition(
            stepId,
            step => step with
            {
                Status = InstallerStepStatus.Completed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Hashes = new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(result.Hashes, StringComparer.Ordinal)),
                BackupPaths = result.BackupPaths.ToArray(),
                FailureCode = null,
                FailureMessage = null,
            });
        return Task.CompletedTask;
    }

    public Task RecordFailedAsync(
        string stepId,
        string failureCode,
        string? safeMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Transition(
            stepId,
            step => step with
            {
                Status = InstallerStepStatus.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                FailureCode = SanitizeIdentifier(failureCode),
                FailureMessage = Redact(safeMessage),
            });
        return Task.CompletedTask;
    }

    public Task RecordRolledBackAsync(string stepId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Transition(
            stepId,
            step => step with
            {
                Status = InstallerStepStatus.RolledBack,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                FailureCode = null,
                FailureMessage = null,
            });
        return Task.CompletedTask;
    }

    public Task RecordRollbackFailedAsync(
        string stepId,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Transition(
            stepId,
            step => step with
            {
                Status = InstallerStepStatus.RollbackFailed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                FailureCode = SanitizeIdentifier(failureCode),
                FailureMessage = "Rollback failed. Use the journal hashes and backup paths for manual recovery.",
            });
        return Task.CompletedTask;
    }

    private void Transition(string stepId, Func<InstallerJournalStep, InstallerJournalStep> update)
    {
        lock (sync)
        {
            var index = FindStepIndex(stepId);
            var steps = Snapshot.Steps.ToArray();
            steps[index] = update(steps[index]);
            Snapshot = Snapshot with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Steps = steps,
            };
            Save();
        }
    }

    private int FindStepIndex(string stepId)
    {
        for (var index = 0; index < Snapshot.Steps.Count; index++)
        {
            if (string.Equals(Snapshot.Steps[index].StepId, stepId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException("Unknown installer step.");
    }

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(JournalPath)!);
        var temporary = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(JournalPath)!,
            "." + System.IO.Path.GetFileName(JournalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var json = JsonSerializer.Serialize(Snapshot, LocalAiJson.Strict);
        File.WriteAllText(temporary, json + Environment.NewLine);
        if (File.Exists(JournalPath))
        {
            File.Replace(temporary, JournalPath, null);
        }
        else
        {
            File.Move(temporary, JournalPath);
        }
    }

    private static void ValidateUniqueSteps(IReadOnlyList<InstallerStepDefinition> steps)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(step.StepId);
            if (!seen.Add(step.StepId))
            {
                throw new ArgumentException("Installer step IDs must be unique.", nameof(steps));
            }
        }
    }

    internal static string? Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var lower = value.ToLowerInvariant();
        if (lower.Contains("token", StringComparison.Ordinal) ||
            lower.Contains("secret", StringComparison.Ordinal) ||
            lower.Contains("password", StringComparison.Ordinal) ||
            lower.Contains("prompt", StringComparison.Ordinal) ||
            lower.Contains("job", StringComparison.Ordinal) ||
            lower.Contains("config_value", StringComparison.Ordinal))
        {
            return "<redacted>";
        }

        return value;
    }

    private static string SanitizeIdentifier(string value) =>
        string.IsNullOrWhiteSpace(value) ? "installer_step_failed" : value.Trim();
}

public sealed class InstallerStepException(string code, string safeMessage) : Exception(safeMessage)
{
    public string Code { get; } = code;
}
