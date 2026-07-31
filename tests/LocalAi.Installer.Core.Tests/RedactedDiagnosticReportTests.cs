using LocalAi.Installer.Core.Diagnostics;

namespace LocalAi.Installer.Core.Tests;

public sealed class RedactedDiagnosticReportTests
{
    [Fact]
    public void Diagnostics_omit_prompts_jobs_tokens_credentials_and_config_values()
    {
        var report = RedactedDiagnosticReport.Create(
            [
                new DiagnosticEntry("status", "installed"),
                new DiagnosticEntry("prompt", "user prompt"),
                new DiagnosticEntry("jobId", "job-123"),
                new DiagnosticEntry("tokenCount", "9999"),
                new DiagnosticEntry("apiKey", "secret"),
                new DiagnosticEntry("config.toml", "approval_policy = never"),
                new DiagnosticEntry("Authorization", "Bearer secret"),
            ]);

        var text = report.ToText();

        Assert.Contains("status=installed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("user prompt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("job-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("9999", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approval_policy", text, StringComparison.Ordinal);
        Assert.Contains("apiKey=<redacted>", text, StringComparison.Ordinal);
        Assert.Contains("Authorization=<redacted>", text, StringComparison.Ordinal);
    }
}
