using System.Text;
using System.Text.Json;
using CodeSearch.Core.Semantics;
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
                $"No current.json under {binRoot}. Nothing is installed here.");
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
                $"current.json cannot be read: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                "current.json names no version.");
        }

        var directory = Path.Combine(binRoot, "versions", version);
        if (!Directory.Exists(directory))
        {
            return new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                $"The pointer names {version} and that directory does not exist.");
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
                $"{version} is missing: {string.Join(", ", missing)}")
            : new DoctorCheck(
                "version",
                DoctorStatus.Ok,
                $"{version}, all {LocalAiPackageLayout.VersionRequiredFiles.Count} binaries present");
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
                $"The stable entry point is missing: {launcher}. Registrations pointing at it " +
                "will fail, and registrations pointing inside a version directory break on the " +
                "next upgrade.");
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
                "Not running. It starts on demand, so this is only worth noting.");
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
                $"host.json cannot be read: {exception.Message}");
        }

        if (state is null)
        {
            return new DoctorCheck("broker", DoctorStatus.Failed, "host.json is empty.");
        }

        var silence = DateTimeOffset.UtcNow - state.HeartbeatAtUtc;
        return silence > TimeSpan.FromMinutes(5)
            ? new DoctorCheck(
                "broker",
                DoctorStatus.Warning,
                $"process {state.ProcessId}, last heartbeat {silence.TotalMinutes:F0} min ago — " +
                "either stopped without clearing host.json, or wedged.")
            : new DoctorCheck(
                "broker",
                DoctorStatus.Ok,
                $"process {state.ProcessId}, heartbeat {silence.TotalSeconds:F0}s ago");
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
                $"{queued} queued, {quarantined} quarantined. Quarantined jobs are never retried.")
            : new DoctorCheck("queue", DoctorStatus.Ok, $"{queued} queued, none quarantined");

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
            Path.Combine(root, BrokerPolicy.FileName),
            () => new ModelResidencyPolicyStore(root).Read(),
            policy => $"residency {policy.ModelResidency}, keep-alive {policy.IdleModelKeepAliveSeconds}s");

        yield return PolicyCheck(
            "policy: retention",
            Path.Combine(root, RuntimeRetentionPolicy.FileName),
            () => new RuntimeRetentionPolicyStore(root).Read(),
            policy =>
                $"{policy.GenerationsPerRepository} generations, " +
                $"{policy.InstalledVersions} versions, " +
                $"telemetry {policy.TelemetryRetentionDays}d");

        yield return PolicyCheck(
            "policy: language servers",
            Path.Combine(root, LanguageServerPolicy.FileName),
            () => new LanguageServerPolicyStore(root).Read(),
            policy => policy.Enabled
                ? $"enabled for {string.Join(", ", policy.Languages.Where(l => l.Value.Enabled).Select(l => l.Key))}"
                : "disabled");

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
                "check disabled; run `localai policy set --update-check on` to look for " +
                "releases");
        }

        var state = new UpdateCheckStateStore(root).Read();
        var installed = InstalledVersion(root);
        return state.Status switch
        {
            UpdateCheckStatus.Verified when state.IsNewerThan(installed) => new DoctorCheck(
                "update",
                DoctorStatus.Warning,
                $"{state.LatestVersion} is available; this installation is {installed}. " +
                state.ReleaseUrl),
            UpdateCheckStatus.Verified => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                $"up to date at {installed} " +
                $"(checked {state.CheckedAtUtc:yyyy-MM-dd HH:mm} UTC)"),
            UpdateCheckStatus.Unavailable => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                "unknown; the last check produced nothing to believe " +
                $"(tried {state.CheckedAtUtc:yyyy-MM-dd HH:mm} UTC)"),
            _ => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                "unknown; nothing has been checked yet"),
        };
    }

    /// <summary>
    /// The version the pointer names, or null. Read here rather than taken from the version
    /// check above so that a broken pointer produces one failure — that check's — rather than
    /// two saying the same thing.
    /// </summary>
    private static string? InstalledVersion(string root)
    {
        try
        {
            var pointerPath = Path.Combine(root, "bin", "current.json");
            if (!File.Exists(pointerPath))
            {
                return null;
            }

            // Read as text, not as bytes: a pointer written with a byte order mark is still a
            // valid document to every other reader of this file, and a version line that went
            // blank over one would be a puzzle with no clue in it.
            using var document = JsonDocument.Parse(File.ReadAllText(pointerPath));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports the policy that is actually in effect, and where it came from.
    ///
    /// It first tried to catch the silent fallback — a policy file that stopped parsing and was
    /// replaced by the defaults without saying so — by noticing that a configured file read back
    /// as the defaults. That cannot work, and produced a false alarm on the first real
    /// installation it saw: a file may legitimately hold exactly the default values, and this
    /// one did. A diagnostic that cries wolf is worse than one check short, because it teaches
    /// its reader to skim.
    ///
    /// Catching that properly needs the stores to distinguish "absent" from "unreadable" instead
    /// of collapsing both into the defaults, which is a change to them and not to this command.
    /// </summary>
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
                    $"{identity.RepositoryRoot} is not connected. " +
                    "Run localai sync --root to index it.");
            }

            return manifest.State == RepositoryIndexState.Current
                ? new DoctorCheck(
                    "repository",
                    DoctorStatus.Ok,
                    $"{manifest.State}, generation {Short(manifest.CurrentGenerationId)}")
                : new DoctorCheck(
                    "repository",
                    DoctorStatus.Warning,
                    $"{manifest.State}, generation {Short(manifest.CurrentGenerationId)}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException
                or InvalidDataException)
        {
            return new DoctorCheck("repository", DoctorStatus.Failed, exception.Message);
        }

        static string Short(string? id) =>
            string.IsNullOrEmpty(id) ? "none" : id[..Math.Min(12, id.Length)];
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
                ? $"{failed} problem(s), {warned} worth reading."
                : warned > 0
                    ? $"No problems. {warned} worth reading."
                    : "No problems.");
        return text.ToString();
    }

    private static string Marker(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "ok  ",
        DoctorStatus.Warning => "note",
        _ => "FAIL",
    };
}
