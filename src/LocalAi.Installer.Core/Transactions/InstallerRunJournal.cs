using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Installer.Core.Transactions;

public enum InstallerRunEffectKind
{
    DependencyInstall,
    PackageActivation,
    ModelInstall,
    ResidencyPolicy,
    OllamaLaunchRecord,
    AgentConfiguration,
}

public enum InstallerRunStepStatus
{
    /// <summary>
    /// The intent was written and the effect is in flight. After a killed process this is
    /// the only trace the effect leaves, which is exactly why the intent is written first:
    /// a journal that records outcomes only has nothing to say about the failure that
    /// prevented the outcome from being recorded.
    /// </summary>
    Running,
    Completed,
    Failed,
    Undone,
    UndoFailed,

    /// <summary>
    /// The target changed after the run, so rollback left it alone. Restoring a file
    /// somebody edited since the installation would trade one surprise for a worse one.
    /// </summary>
    UndoSkipped,
}

public enum InstallerRunOutcome
{
    Completed,
    Failed,
    Cancelled,
    RolledBack,
    RollbackIncomplete,

    /// <summary>
    /// The user saw what an interrupted run left behind and chose to keep it. Recorded so
    /// the next wizard start does not ask the same question again.
    /// </summary>
    Abandoned,
}

/// <summary>
/// Everything rollback needs to put one file back: the pre-install content lives either
/// inline (small runtime files) or in an on-disk backup the effect already wrote (agent
/// configurations). Both carry hashes, because a restore that cannot prove it is restoring
/// the right bytes is a mutation, not a rollback.
/// </summary>
public sealed record InstallerRunFileUndo(
    string Path,
    bool ExistedBefore,
    string BeforeSha256,
    string? BeforeContentBase64,
    string? BackupPath,
    string AfterSha256);

public sealed record InstallerRunUndoData(
    string? ActivatedVersion = null,
    string? PriorVersion = null,
    IReadOnlyList<InstallerRunFileUndo>? Files = null);

public sealed record InstallerRunStep(
    string StepId,
    InstallerRunEffectKind Kind,
    string Description,
    InstallerRunStepStatus Status,
    bool IsReversible,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? Detail,
    InstallerRunUndoData? Undo);

public sealed record InstallerRunJournalSnapshot(
    int SchemaVersion,
    Guid RunId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    InstallerRunOutcome? Outcome,
    IReadOnlyList<InstallerRunStep> Steps)
{
    /// <summary>An outcome was never written, so the process died before its own cleanup.</summary>
    public bool IsInterrupted => Outcome is null;

    public bool HasReversibleWork => Steps.Any(step =>
        step.Status == InstallerRunStepStatus.Completed && step.IsReversible);
}

/// <summary>
/// A durable record of what one wizard run did to the machine.
///
/// The run log answers "what happened"; this answers "what is there now and how to take it
/// back". It lives outside %LOCALAPPDATA%\LocalAi on purpose: that tree is validated
/// against an exact name list on every install, so a journal inside it would make the next
/// installation refuse the layout it is trying to repair. Every mutation is flushed to disk
/// before the effect it describes proceeds — the failure this exists for is the one where
/// the wizard never gets to run its own cleanup.
/// </summary>
public sealed class InstallerRunJournal
{
    public const int CurrentSchemaVersion = 1;
    private const string FilePrefix = "journal-";

    /// <summary>
    /// Inline pre-install content is meant for the runtime's small JSON files. Anything
    /// larger has an on-disk backup instead; a cap keeps a pathological config from turning
    /// the journal into a copy of it.
    /// </summary>
    public const int MaximumInlineContentBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Serializer = CreateSerializer();

    private readonly object sync = new();

    private InstallerRunJournal(string journalPath, InstallerRunJournalSnapshot snapshot)
    {
        JournalPath = journalPath;
        Snapshot = snapshot;
    }

    public string JournalPath { get; }

    public InstallerRunJournalSnapshot Snapshot { get; private set; }

    public static InstallerRunJournal Start(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var path = System.IO.Path.Combine(
            directory,
            $"{FilePrefix}{now.UtcDateTime:yyyyMMdd-HHmmss}-{runId:N}.json");
        var journal = new InstallerRunJournal(
            path,
            new(CurrentSchemaVersion, runId, now, now, null, []));
        journal.Save();
        return journal;
    }

    public static InstallerRunJournal Load(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        InstallerRunJournalSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<InstallerRunJournalSnapshot>(
                File.ReadAllText(journalPath),
                Serializer);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The installer run journal is invalid.", exception);
        }

        if (snapshot is null || snapshot.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("The installer run journal is invalid.");
        }

        return new InstallerRunJournal(journalPath, snapshot);
    }

    /// <summary>
    /// The newest journal whose run never wrote an outcome — a wizard that was killed, or a
    /// machine that lost power, mid-installation. Unreadable files are skipped rather than
    /// fatal: a corrupt journal must not block the installation that could fix the machine,
    /// and there is nothing rollback could do with it anyway.
    /// </summary>
    public static InstallerRunJournal? FindInterrupted(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*.json")
                     .OrderByDescending(System.IO.Path.GetFileName, StringComparer.Ordinal))
        {
            InstallerRunJournal journal;
            try
            {
                journal = Load(path);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or
                    UnauthorizedAccessException)
            {
                continue;
            }

            if (journal.Snapshot.IsInterrupted)
            {
                return journal;
            }
        }

        return null;
    }

    /// <summary>Writes the intent to disk and returns the step id. The effect runs after this.</summary>
    public string BeginStep(InstallerRunEffectKind kind, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        lock (sync)
        {
            var stepId = $"step-{Snapshot.Steps.Count + 1}";
            var now = DateTimeOffset.UtcNow;
            Mutate(snapshot => snapshot with
            {
                Steps =
                [
                    .. snapshot.Steps,
                    new InstallerRunStep(
                        stepId,
                        kind,
                        description,
                        InstallerRunStepStatus.Running,
                        false,
                        now,
                        null,
                        null,
                        null),
                ],
            });
            return stepId;
        }
    }

    public void CompleteStep(
        string stepId,
        string detail,
        bool isReversible,
        InstallerRunUndoData? undo = null) =>
        Transition(stepId, step => step with
        {
            Status = InstallerRunStepStatus.Completed,
            IsReversible = isReversible,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Detail = detail,
            Undo = undo,
        });

    public void FailStep(string stepId, string detail) =>
        Transition(stepId, step => step with
        {
            Status = InstallerRunStepStatus.Failed,
            IsReversible = false,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Detail = detail,
        });

    public void MarkUndoOutcome(string stepId, InstallerRunStepStatus status, string detail)
    {
        if (status is not (InstallerRunStepStatus.Undone or
            InstallerRunStepStatus.UndoFailed or InstallerRunStepStatus.UndoSkipped))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Transition(stepId, step => step with
        {
            Status = status,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Detail = detail,
        });
    }

    public void Finish(InstallerRunOutcome outcome)
    {
        lock (sync)
        {
            Mutate(snapshot => snapshot with { Outcome = outcome });
        }
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private void Transition(string stepId, Func<InstallerRunStep, InstallerRunStep> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        lock (sync)
        {
            var steps = Snapshot.Steps.ToArray();
            var index = Array.FindIndex(steps, step =>
                string.Equals(step.StepId, stepId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException("Unknown installer run journal step.");
            }

            steps[index] = update(steps[index]);
            Mutate(snapshot => snapshot with { Steps = steps });
        }
    }

    private void Mutate(Func<InstallerRunJournalSnapshot, InstallerRunJournalSnapshot> update)
    {
        Snapshot = update(Snapshot) with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        Save();
    }

    /// <summary>
    /// Atomic and write-through. The journal is consulted precisely when the process that
    /// wrote it died without warning, so a half-written or cached-but-unflushed file would
    /// fail at the only moment the journal matters.
    /// </summary>
    private void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(JournalPath)!;
        var temporary = System.IO.Path.Combine(
            directory,
            "." + System.IO.Path.GetFileName(JournalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var json = JsonSerializer.Serialize(Snapshot, Serializer);
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(JournalPath))
            {
                File.Replace(temporary, JournalPath, null);
            }
            else
            {
                File.Move(temporary, JournalPath);
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

    private static JsonSerializerOptions CreateSerializer()
    {
        // String enums and indentation because this file is read by a person standing in
        // front of a machine an installer half-changed; strictness because a journal is
        // evidence, and evidence with unexplained extra fields is not.
        var options = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
