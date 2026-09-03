using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeSearch.Core.Semantics;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Repository;

namespace LocalAi.Cli;

public enum DoctorStatus
{
    Ok,
    Warning,
    Failed
}

public sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail);

public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
{
    /// <summary>
    /// Non-zero only for a real fault. A warning is something worth reading — a large runtime,
    /// a stopped broker — and a diagnostic that exits non-zero for those teaches whoever wired
    /// it into a script to ignore the exit code.
    /// </summary>
    public int ExitCode => Checks.Any(check => check.Status == DoctorStatus.Failed) ? 1 : 0;
}

/// <summary>
/// One command for the sequence anyone verifying an installation runs by hand: which version the
/// pointer names, whether its binaries are there and agree with it, whether the broker is alive,
/// whether the policy files still parse, and how much of the runtime is reclaimable.
///
/// It exists because that sequence was being retyped. Six separate commands, read and compared by
/// eye, is a check nobody performs after the third time — and the parts most worth checking are
/// the ones that fail silently. A policy file that stopped parsing does not announce itself: it
/// falls back to safe defaults and the installation quietly stops doing what it was configured
/// to do.
///
/// Deliberately read-only, and deliberately does not start anything. A diagnostic that launches
/// the broker to see whether the broker is running answers a question nobody asked.
/// </summary>
public static class DoctorCommand
{
    public static int Execute(string runtimeRoot, string? repositoryRoot, TextWriter output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(output);
        var report = Inspect(runtimeRoot, repositoryRoot);
        output.Write(Render(report));
        return report.ExitCode;
    }

    public static DoctorReport Inspect(string runtimeRoot, string? repositoryRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var root = Path.GetFullPath(runtimeRoot);
        var checks = new List<DoctorCheck>
        {
            CheckInstalledVersion(root),
            CheckLauncher(root),
            CheckBroker(root),
            CheckQueue(root),
        };
        checks.AddRange(CheckPolicies(root));
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            checks.Add(CheckRepository(root, repositoryRoot));
        }

        return new DoctorReport(checks);
    }

    private static DoctorCheck CheckInstalledVersion(string root)
    {
        var binRoot = Path.Combine(root, "bin");
        var pointerPath = Path.Combine(binRoot, "current.json");
        if (!File.Exists(pointerPath))
        {
            return new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                CliText.VersionNoPointer(binRoot));
        }

        string? version;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(pointerPath));
            version = document.RootElement.TryGetProperty("version", out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                CliText.VersionPointerUnreadable(exception.Message));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                CliText.VersionPointerEmpty);
        }

        var directory = Path.Combine(binRoot, "versions", version);
        if (!Directory.Exists(directory))
        {
            return new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                CliText.VersionDirectoryMissing(version));
        }

        // The pointer agreeing with a directory that is missing half its binaries is the shape a
        // half-finished install leaves behind, and every tool then fails one at a time.
        var missing = LocalAiPackageLayout.VersionRequiredFiles
            .Where(file => !File.Exists(Path.Combine(directory, file)))
            .ToArray();
        return missing.Length > 0
            ? new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                CliText.VersionFilesMissing(version, string.Join(", ", missing)))
            : new DoctorCheck(
                "version",
                DoctorStatus.Ok,
                CliText.VersionComplete(version, LocalAiPackageLayout.VersionRequiredFiles.Count));
    }

    private static DoctorCheck CheckLauncher(string root)
    {
        var launcher = Path.Combine(
            root,
            "bin",
            "launcher",
            LocalAiPackageLayout.StableLauncherFile);
        return File.Exists(launcher)
            ? new DoctorCheck("launcher", DoctorStatus.Ok, launcher)
            : new DoctorCheck(
                "launcher",
                DoctorStatus.Failed,
                CliText.LauncherMissing(launcher));
    }

    private static DoctorCheck CheckBroker(string root)
    {
        var hostPath = Path.Combine(root, "host.json");
        if (!File.Exists(hostPath))
        {
            // Not a fault. The broker starts on demand, so no host.json simply means nothing has
            // needed a model since it last stopped.
            return new DoctorCheck(
                "broker",
                DoctorStatus.Warning,
                CliText.BrokerNotRunning);
        }

        BrokerProcessState? state;
        try
        {
            state = JsonSerializer.Deserialize<BrokerProcessState>(
                File.ReadAllBytes(hostPath),
                LocalAiJson.Strict);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new DoctorCheck(
                "broker",
                DoctorStatus.Failed,
                CliText.BrokerStateUnreadable(exception.Message));
        }

        if (state is null)
        {
            return new DoctorCheck("broker", DoctorStatus.Failed, CliText.BrokerStateEmpty);
        }

        var silence = DateTimeOffset.UtcNow - state.HeartbeatAtUtc;
        return silence > TimeSpan.FromMinutes(5)
            ? new DoctorCheck(
                "broker",
                DoctorStatus.Warning,
                CliText.BrokerSilent(
                    state.ProcessId,
                    silence.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)))
            : new DoctorCheck(
                "broker",
                DoctorStatus.Ok,
                CliText.BrokerAlive(
                    state.ProcessId,
                    silence.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)));
    }

    private static DoctorCheck CheckQueue(string root)
    {
        var queued = Count(Path.Combine(root, "jobs"));
        var quarantined = Count(Path.Combine(root, "quarantine"));

        // Quarantine is the interesting number. A job lands there when it could not be parsed or
        // recovered, and nothing raises it: the queue keeps working and the entry sits for months.
        return quarantined > 0
            ? new DoctorCheck(
                "queue",
                DoctorStatus.Warning,
                CliText.QueueQuarantined(queued, quarantined))
            : new DoctorCheck("queue", DoctorStatus.Ok, CliText.QueueClean(queued));

        static int Count(string path) =>
            Directory.Exists(path)
                ? Directory.EnumerateFileSystemEntries(path).Count()
                : 0;
    }

    /// <summary>
    /// Every policy store answers a malformed file with its safe defaults rather than an error,
    /// which is right at runtime and invisible to the operator: the file is still on disk, still
    /// looks configured, and no longer does anything. Reading each one back and comparing against
    /// the defaults is the only way to see it.
    /// </summary>
    private static IEnumerable<DoctorCheck> CheckPolicies(string root)
    {
        yield return PolicyCheck(
            "policy: models",
            RuntimeDirectories.SettingsFile(root, BrokerPolicy.FileName),
            () => new ModelResidencyPolicyStore(root).Read(),
            policy => CliText.PolicyModels(
                policy.ModelResidency,
                policy.IdleModelKeepAliveSeconds));

        yield return PolicyCheck(
            "policy: retention",
            RuntimeDirectories.SettingsFile(root, RuntimeRetentionPolicy.FileName),
            () => new RuntimeRetentionPolicyStore(root).Read(),
            policy => CliText.PolicyRetention(
                policy.GenerationsPerRepository,
                policy.InstalledVersions,
                policy.TelemetryRetentionDays));

        yield return PolicyCheck(
            "policy: language servers",
            RuntimeDirectories.SettingsFile(root, LanguageServerPolicy.FileName),
            () => new LanguageServerPolicyStore(root).Read(),
            policy => policy.Enabled
                ? CliText.PolicyLanguageServersEnabled(string.Join(
                    ", ",
                    policy.Languages.Where(l => l.Value.Enabled).Select(l => l.Key)))
                : CliText.PolicyLanguageServersDisabled);

        yield return CheckUpdates(root);
    }

    /// <summary>
    /// What the last update check found, read from the state file the broker writes.
    ///
    /// Never a network call: `doctor` is a question about this machine, and a diagnostic that
    /// quietly reached the internet would be a second, unthrottled caller of the thing the
    /// policy exists to ration. A newer release is a warning rather than a failure — an
    /// installation a version behind is working perfectly well, and a diagnostic that exits
    /// non-zero for it teaches whoever wired it into a script to ignore the exit code.
    /// </summary>
    private static DoctorCheck CheckUpdates(string root)
    {
        var policy = new UpdateCheckPolicyStore(root).Read();
        if (!policy.Enabled)
        {
            return new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateCheckDisabled);
        }

        var state = new UpdateCheckStateStore(root).Read();
        var installed = InstalledVersionReader.Read(root);
        return UpdateComparison.Compare(state, installed) switch
        {
            UpdateAvailability.Available => new DoctorCheck(
                "update",
                DoctorStatus.Warning,
                CliText.UpdateAvailable(
                    state.LatestVersion,
                    installed.DisplayName,
                    state.ReleaseUrl)),
            UpdateAvailability.UpToDate => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateUpToDate(
                    installed.DisplayName,
                    state.CheckedAtUtc?.ToString(
                        "yyyy-MM-dd HH:mm",
                        CultureInfo.InvariantCulture))),
            _ when state.Status == UpdateCheckStatus.Unavailable => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateUnknownUnavailable(state.CheckedAtUtc?.ToString(
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture))),
            // Verified, but nothing here can be compared against it: an installation made
            // before the release version was recorded knows only its directory name. Said
            // plainly, because answering "up to date" from a comparison that failed is the
            // defect this check was rewritten for.
            _ when state.Status == UpdateCheckStatus.Verified => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateIncomparable(state.LatestVersion)),
            _ => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateNeverChecked),
        };
    }

    private static DoctorCheck PolicyCheck<T>(
        string name,
        string path,
        Func<T> read,
        Func<T, string> describe)
    {
        try
        {
            var policy = read();
            return new DoctorCheck(
                name,
                DoctorStatus.Ok,
                File.Exists(path)
                    ? describe(policy)
                    : $"{describe(policy)} (defaults, no file)");
        }
        catch (Exception exception)
        {
            return new DoctorCheck(name, DoctorStatus.Failed, exception.Message);
        }
    }

    private static DoctorCheck CheckRepository(string runtimeRoot, string? repositoryRoot)
    {
        try
        {
            var identity = CodeSearch.Core.Indexing.RuntimeIndexLayout.Inspect(
                repositoryRoot!,
                runtimeRoot);
            var manifest = new RepositoryManifestStore(identity.RepositoryRuntimeRoot).Read();
            if (manifest is null)
            {
                return new DoctorCheck(
                    "repository",
                    DoctorStatus.Warning,
                    CliText.RepositoryNotConnected(identity.RepositoryRoot.Value));
            }

            return manifest.State == RepositoryIndexState.Current
                ? new DoctorCheck(
                    "repository",
                    DoctorStatus.Ok,
                    CliText.RepositoryState(
                        manifest.State,
                        Short(manifest.CurrentGenerationId)))
                : new DoctorCheck(
                    "repository",
                    DoctorStatus.Warning,
                    CliText.RepositoryState(
                        manifest.State,
                        Short(manifest.CurrentGenerationId)));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException
                or InvalidDataException)
        {
            return new DoctorCheck("repository", DoctorStatus.Failed, exception.Message);
        }

        static string Short(string? id) =>
            string.IsNullOrEmpty(id) ? CliText.GenerationNone : id[..Math.Min(12, id.Length)];
    }

    public static string Render(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var width = report.Checks.Max(check => check.Name.Length);
        var text = new StringBuilder();
        foreach (var check in report.Checks)
        {
            text.Append(Marker(check.Status))
                .Append("  ")
                .Append(check.Name.PadRight(width))
                .Append("  ")
                .AppendLine(check.Detail);
        }

        var failed = report.Checks.Count(check => check.Status == DoctorStatus.Failed);
        var warned = report.Checks.Count(check => check.Status == DoctorStatus.Warning);
        text.AppendLine();
        text.AppendLine(
            failed > 0
                ? CliText.SummaryProblems(failed, warned)
                : warned > 0
                    ? CliText.SummaryNoProblemsWithNotes(warned)
                    : CliText.SummaryNoProblems);
        return text.ToString();
    }

    private static string Marker(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "ok  ",
        DoctorStatus.Warning => "note",
        _ => "FAIL",
    };
}
