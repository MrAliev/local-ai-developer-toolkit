using System.Text;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Diagnostics;

public sealed record DiagnosticEntry(string Key, string Value);

public sealed class RedactedDiagnosticReport
{
    private readonly string text;

    private RedactedDiagnosticReport(string text)
    {
        this.text = text;
    }

    public static RedactedDiagnosticReport Create(
        IReadOnlyList<DiagnosticEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (IsOmittedKey(entry.Key))
            {
                continue;
            }

            builder.Append(entry.Key);
            builder.Append('=');
            builder.AppendLine(IsSensitiveKey(entry.Key) ? "<redacted>" : InstallerJournal.Redact(entry.Value));
        }

        return new(builder.ToString());
    }

    public static RedactedDiagnosticReport Create(
        InstallerJournalSnapshot snapshot,
        IReadOnlyDictionary<string, string> details)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(details);

        var builder = new StringBuilder();
        builder.AppendLine("LocalAi installer diagnostic report");
        builder.AppendLine($"schemaVersion={snapshot.SchemaVersion}");
        builder.AppendLine($"transactionId={snapshot.TransactionId}");
        builder.AppendLine($"planId={snapshot.PlanId}");
        builder.AppendLine($"updatedAtUtc={snapshot.UpdatedAtUtc:O}");
        foreach (var step in snapshot.Steps)
        {
            builder.AppendLine(
                $"step={step.StepId}; effect={step.EffectKind}; transactional={step.IsTransactional}; status={step.Status}; attempts={step.Attempts}");
            foreach (var hash in step.Hashes.OrderBy(hash => hash.Key, StringComparer.Ordinal))
            {
                builder.AppendLine($"{hash.Key}={hash.Value}");
            }

            foreach (var backup in step.BackupPaths)
            {
                builder.AppendLine($"backupPath={backup}");
            }

            if (!string.IsNullOrWhiteSpace(step.FailureCode))
            {
                builder.AppendLine($"failureCode={step.FailureCode}");
            }

            var safeFailure = InstallerJournal.Redact(step.FailureMessage);
            if (!string.IsNullOrWhiteSpace(safeFailure) &&
                !string.Equals(safeFailure, "<redacted>", StringComparison.Ordinal))
            {
                builder.AppendLine($"failureMessage={safeFailure}");
            }
        }

        foreach (var effect in snapshot.NonTransactionalEffects)
        {
            builder.AppendLine($"nonTransactional={effect.StepId}; effect={effect.EffectKind}");
        }

        foreach (var detail in details.OrderBy(detail => detail.Key, StringComparer.Ordinal))
        {
            if (IsSensitiveKey(detail.Key))
            {
                continue;
            }

            var safeValue = InstallerJournal.Redact(detail.Value);
            if (!string.IsNullOrWhiteSpace(safeValue) &&
                !string.Equals(safeValue, "<redacted>", StringComparison.Ordinal))
            {
                builder.AppendLine($"{detail.Key}={safeValue}");
            }
        }

        return new(builder.ToString());
    }

    public string ToText() => text;

    private static bool IsOmittedKey(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.Contains("prompt", StringComparison.Ordinal) ||
               lower.Contains("job", StringComparison.Ordinal) ||
               lower.Contains("token", StringComparison.Ordinal) ||
               lower.Contains("config.toml", StringComparison.Ordinal) ||
               lower.Contains("config_value", StringComparison.Ordinal);
    }

    private static bool IsSensitiveKey(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.Contains("authorization", StringComparison.Ordinal) ||
               lower.Contains("credential", StringComparison.Ordinal) ||
               lower.Contains("secret", StringComparison.Ordinal) ||
               lower.Contains("password", StringComparison.Ordinal) ||
               lower.Contains("api_key", StringComparison.Ordinal) ||
               lower.Contains("apikey", StringComparison.Ordinal) ||
               string.Equals(lower, "config", StringComparison.Ordinal);
    }
}
