using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Transactions;

public enum InstallerRollbackStepOutcome
{
    Undone,

    /// <summary>Irreversible by policy: the report must say it stayed, never imply it went.</summary>
    LeftInPlace,

    /// <summary>The target no longer looks like what the run wrote, so nothing was touched.</summary>
    Skipped,

    Failed,
}

public sealed record InstallerRollbackStepReport(
    string StepId,
    InstallerRunEffectKind Kind,
    string Description,
    InstallerRollbackStepOutcome Outcome,
    string Detail);

public sealed record InstallerRollbackReport(
    bool AllReversibleUndone,
    IReadOnlyList<InstallerRollbackStepReport> Steps);

/// <summary>
/// Undoes what a journalled run recorded as reversible, newest effect first, and names what
/// it will not touch.
///
/// The boundary is honesty, not ambition. A winget or npm install is shared machine software
/// this installer cannot claim back; a pulled model belongs to Ollama and to whatever else
/// uses it; a first installation's root directory holds runtime state (indexes included) the
/// moment the broker starts. Those are reported as left in place. What is undone is what the
/// run can prove it changed: the active-version pointer, and files whose current content
/// still hashes to what the run wrote.
/// </summary>
public sealed class InstallerRunRollback(
    IProcessRunner processRunner,
    InstallationLayout layout,
    TimeSpan activationTimeout)
{
    private readonly IProcessRunner processRunner =
        processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    private readonly InstallationLayout layout =
        layout ?? throw new ArgumentNullException(nameof(layout));

    public async Task<InstallerRollbackReport> RollbackAsync(
        InstallerRunJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var reports = new List<InstallerRollbackStepReport>();
        var anyUndoFailed = false;
        foreach (var step in journal.Snapshot.Steps.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, detail) = step.Status switch
            {
                InstallerRunStepStatus.Running => (
                    InstallerRollbackStepOutcome.LeftInPlace,
                    "The step began and never finished, so its state is unknown; nothing " +
                    "was touched. Check it by hand."),
                InstallerRunStepStatus.Failed => (
                    InstallerRollbackStepOutcome.Skipped,
                    "The step failed during the run and recorded nothing to undo."),
                InstallerRunStepStatus.Undone => (
                    InstallerRollbackStepOutcome.Undone,
                    "Already undone by an earlier rollback."),
                InstallerRunStepStatus.UndoSkipped => (
                    InstallerRollbackStepOutcome.Skipped,
                    step.Detail ?? "Left alone by an earlier rollback."),
                InstallerRunStepStatus.UndoFailed => (
                    InstallerRollbackStepOutcome.Failed,
                    step.Detail ?? "An earlier rollback failed on this step."),
                _ when !step.IsReversible => (
                    InstallerRollbackStepOutcome.LeftInPlace,
                    step.Detail is { Length: > 0 } completedDetail
                        ? $"Not undone by this installer. {completedDetail}"
                        : "Not undone by this installer."),
                _ => await UndoAsync(step, cancellationToken).ConfigureAwait(false),
            };

            if (step.Status == InstallerRunStepStatus.Completed)
            {
                journal.MarkUndoOutcome(step.StepId, ToStepStatus(outcome), detail);
            }

            anyUndoFailed |= outcome == InstallerRollbackStepOutcome.Failed;
            reports.Add(new(step.StepId, step.Kind, step.Description, outcome, detail));
        }

        journal.Finish(anyUndoFailed
            ? InstallerRunOutcome.RollbackIncomplete
            : InstallerRunOutcome.RolledBack);
        return new(!anyUndoFailed, reports);
    }

    private static InstallerRunStepStatus ToStepStatus(InstallerRollbackStepOutcome outcome) =>
        outcome switch
        {
            InstallerRollbackStepOutcome.Undone => InstallerRunStepStatus.Undone,
            InstallerRollbackStepOutcome.Failed => InstallerRunStepStatus.UndoFailed,
            _ => InstallerRunStepStatus.UndoSkipped,
        };

    private async Task<(InstallerRollbackStepOutcome Outcome, string Detail)> UndoAsync(
        InstallerRunStep step,
        CancellationToken cancellationToken)
    {
        try
        {
            return step.Kind switch
            {
                InstallerRunEffectKind.PackageActivation =>
                    await UndoActivationAsync(step, cancellationToken).ConfigureAwait(false),
                InstallerRunEffectKind.ResidencyPolicy or
                    InstallerRunEffectKind.OllamaLaunchRecord or
                    InstallerRunEffectKind.AgentConfiguration => UndoFiles(step),
                _ => (
                    InstallerRollbackStepOutcome.Failed,
                    "The journal marks this step reversible but no undo procedure exists " +
                    "for its kind."),
            };
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            (exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException or FormatException or
                InvalidOperationException))
        {
            return (InstallerRollbackStepOutcome.Failed, exception.Message);
        }
    }

    /// <summary>
    /// Returns the machine to the version that was active before the run, through the same
    /// launcher verb and the same guarded swap the installation itself used. The guard is
    /// the point: <c>--if-current-sha256</c> makes the launcher refuse to move a pointer
    /// that no longer says what this run wrote, so a version somebody activated in between
    /// is never overwritten by a stale rollback.
    /// </summary>
    private async Task<(InstallerRollbackStepOutcome, string)> UndoActivationAsync(
        InstallerRunStep step,
        CancellationToken cancellationToken)
    {
        if (step.Undo is not { ActivatedVersion: { Length: > 0 } activated,
                PriorVersion: { Length: > 0 } prior })
        {
            return (
                InstallerRollbackStepOutcome.Failed,
                "The journal does not name the versions involved.");
        }

        CurrentPointerSnapshot pointer;
        try
        {
            pointer = ReadPointer();
        }
        catch (Exception exception) when (
            exception is CurrentPointerException or ActivationCoordinationException or
                IOException or UnauthorizedAccessException)
        {
            return (
                InstallerRollbackStepOutcome.Failed,
                $"The current-version pointer could not be read: {exception.Message}");
        }

        if (!pointer.Exists || !pointer.IsCanonical ||
            !string.Equals(pointer.Version, activated, StringComparison.Ordinal))
        {
            return (
                InstallerRollbackStepOutcome.Skipped,
                $"The active version is no longer {activated}; the pointer was left alone.");
        }

        if (!File.Exists(layout.LauncherPath))
        {
            return (
                InstallerRollbackStepOutcome.Failed,
                "The launcher that performs activation is missing.");
        }

        var result = await processRunner.RunAsync(
                layout.LauncherPath,
                [
                    "activate", prior, "--stop-running", "--if-current-sha256",
                    pointer.Sha256Hex,
                ],
                activationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode is not 0 || result.TimedOut || result.Cancelled)
        {
            return (
                InstallerRollbackStepOutcome.Failed,
                $"The launcher refused to reactivate {prior} " +
                $"(exit code {result.ExitCode?.ToString() ?? "none"}).");
        }

        CurrentPointerSnapshot after;
        try
        {
            after = ReadPointer();
        }
        catch (Exception exception) when (
            exception is CurrentPointerException or ActivationCoordinationException or
                IOException or UnauthorizedAccessException)
        {
            return (
                InstallerRollbackStepOutcome.Failed,
                $"Reactivation ran but could not be verified: {exception.Message}");
        }

        return after.Exists && string.Equals(after.Version, prior, StringComparison.Ordinal)
            ? (
                InstallerRollbackStepOutcome.Undone,
                $"Version {prior} is active again. The {activated} version directory and " +
                "the updated launcher stay on disk: versions are immutable and the " +
                "launcher is the stable entry point.")
            : (
                InstallerRollbackStepOutcome.Failed,
                $"The launcher reported success but {prior} is not the active version.");
    }

    private CurrentPointerSnapshot ReadPointer()
    {
        // Shared, never exclusive: every connected client holds a shared lease for its
        // whole lifetime, and an exclusive read would refuse rollback on exactly the
        // machines where LocalAi is in use. Writers still serialise inside the launcher.
        using var lease = ActivationCoordinator.AcquireShared(layout.BinRoot, activationTimeout);
        return CurrentPointerSnapshot.Read(lease);
    }

    private static (InstallerRollbackStepOutcome, string) UndoFiles(InstallerRunStep step)
    {
        if (step.Undo?.Files is not { Count: > 0 } files)
        {
            return (
                InstallerRollbackStepOutcome.Failed,
                "The journal records no files to restore.");
        }

        var notes = new List<string>();
        var anyFailed = false;
        var anySkipped = false;
        foreach (var file in files.Reverse())
        {
            var (outcome, note) = RestoreFile(file);
            anyFailed |= outcome == InstallerRollbackStepOutcome.Failed;
            anySkipped |= outcome == InstallerRollbackStepOutcome.Skipped;
            notes.Add($"{file.Path}: {note}");
        }

        var overall = anyFailed
            ? InstallerRollbackStepOutcome.Failed
            : anySkipped
                ? InstallerRollbackStepOutcome.Skipped
                : InstallerRollbackStepOutcome.Undone;
        return (overall, string.Join(" ", notes));
    }

    private static (InstallerRollbackStepOutcome, string) RestoreFile(InstallerRunFileUndo file)
    {
        try
        {
            var exists = File.Exists(file.Path);
            var currentSha = exists
                ? InstallerRunJournal.Sha256Hex(File.ReadAllBytes(file.Path))
                : null;

            if (!file.ExistedBefore)
            {
                if (!exists)
                {
                    return (InstallerRollbackStepOutcome.Undone, "already absent.");
                }

                if (!string.Equals(currentSha, file.AfterSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return (
                        InstallerRollbackStepOutcome.Skipped,
                        "changed after the installation; left alone.");
                }

                File.Delete(file.Path);
                return (InstallerRollbackStepOutcome.Undone, "removed.");
            }

            if (exists && string.Equals(currentSha, file.BeforeSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    InstallerRollbackStepOutcome.Undone,
                    "already holds the pre-install content.");
            }

            if (exists && !string.Equals(currentSha, file.AfterSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    InstallerRollbackStepOutcome.Skipped,
                    "changed after the installation; left alone.");
            }

            // The file still holds what the run wrote, or vanished since. Either way the
            // pre-install content is the right thing to put back, and only a copy that
            // hashes to the recorded value qualifies as that content.
            var source = ReadRestoreSource(file);
            if (source is null ||
                !string.Equals(
                    InstallerRunJournal.Sha256Hex(source),
                    file.BeforeSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return (
                    InstallerRollbackStepOutcome.Failed,
                    "no copy matching the recorded pre-install content is available.");
            }

            WriteAtomically(file.Path, source);
            return (InstallerRollbackStepOutcome.Undone, "restored the pre-install content.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException or FormatException)
        {
            return (InstallerRollbackStepOutcome.Failed, exception.Message);
        }
    }

    private static byte[]? ReadRestoreSource(InstallerRunFileUndo file)
    {
        if (file.BackupPath is { Length: > 0 } backupPath && File.Exists(backupPath))
        {
            return File.ReadAllBytes(backupPath);
        }

        return file.BeforeContentBase64 is { Length: > 0 } inline
            ? Convert.FromBase64String(inline)
            : null;
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
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
}
