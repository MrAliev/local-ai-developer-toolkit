using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>
/// One fact a check established, for the face that is parsed rather than read.
///
/// The prose states these inside sentences — "process 24188, heartbeat 3s ago" — and a caller
/// is told never to parse a sentence, so what a check knows as a number, an identifier or a
/// path is published as itself. Nothing here is derived: a fact exists only where the check
/// already had it in hand.
/// </summary>
public sealed record DoctorFact(string Name, object? Value);

public sealed record DoctorCheck(
    string Name,
    DoctorStatus Status,
    string Detail,
    IReadOnlyList<DoctorFact>? Facts = null);

public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
{
    /// <summary>
    /// Non-zero only for a real fault. A warning is something worth reading — a large runtime,
    /// a stopped broker — and a diagnostic that exits non-zero for those teaches whoever wired
    /// it into a script to ignore the exit code.
    /// </summary>
    public int ExitCode => Checks.Any(check => check.Status == DoctorStatus.Failed) ? 1 : 0;

    /// <summary>The report in one word, for a caller that wants the answer before the detail.</summary>
    public DoctorStatus Verdict =>
        Checks.Any(check => check.Status == DoctorStatus.Failed)
            ? DoctorStatus.Failed
            : Checks.Any(check => check.Status == DoctorStatus.Warning)
                ? DoctorStatus.Warning
                : DoctorStatus.Ok;
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

        var release = InstalledVersionReader.Read(root).ReleaseVersion;
        var directory = Path.Combine(binRoot, "versions", version);
        if (!Directory.Exists(directory))
        {
            return new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                CliText.VersionDirectoryMissing(version),
                Facts(("versionDirectory", version), ("releaseVersion", release)));
        }

        // The pointer agreeing with a directory that is missing half its binaries is the shape a
        // half-finished install leaves behind, and every tool then fails one at a time.
        var missing = LocalAiPackageLayout.VersionRequiredFiles
            .Where(file => !File.Exists(Path.Combine(directory, file)))
            .ToArray();
        var versionFacts = Facts(
            ("versionDirectory", version),
            ("releaseVersion", release),
            ("missingFiles", missing));
        return missing.Length > 0
            ? new DoctorCheck(
                "version",
                DoctorStatus.Failed,
                CliText.VersionFilesMissing(version, string.Join(", ", missing)),
                versionFacts)
            : new DoctorCheck(
                "version",
                DoctorStatus.Ok,
                CliText.VersionComplete(version, LocalAiPackageLayout.VersionRequiredFiles.Count),
                versionFacts);
    }

    private static DoctorCheck CheckLauncher(string root)
    {
        var launcher = Path.Combine(
            root,
            "bin",
            "launcher",
            LocalAiPackageLayout.StableLauncherFile);
        return File.Exists(launcher)
            ? new DoctorCheck(
                "launcher",
                DoctorStatus.Ok,
                launcher,
                Facts(("path", launcher)))
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
                    silence.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)),
                BrokerFacts(state, silence));
    }

    /// <summary>
    /// The age past which a queued job that has never been attempted is a stall rather than a
    /// wait. Not invented here: it is the age at which the scheduler itself force-includes a
    /// starved job, so anything older has already been passed over by the one component whose job
    /// is not to pass it over.
    /// </summary>
    private static readonly TimeSpan StallAge = TimeSpan.FromMinutes(15);

    private static DoctorCheck CheckQueue(string root)
    {
        var queued = Count(Path.Combine(root, "jobs"));
        var quarantined = Count(Path.Combine(root, "quarantine"));
        var stalled = OldestUnattempted(Path.Combine(root, "jobs"));

        // A stopped queue looked exactly like a busy one here: this check counted directories and
        // said "4 queued, none quarantined" every minute for two hours while nothing moved (#335).
        // The broker was healthy by its own measures throughout — the queue was not.
        if (stalled is { } age && age >= StallAge)
        {
            return new DoctorCheck(
                "queue",
                DoctorStatus.Warning,
                CliText.QueueStalled(
                    queued,
                    ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture)),
                QueueFacts(queued, quarantined, stalled));
        }

        // Quarantine is the interesting number. A job lands there when it could not be parsed or
        // recovered, and nothing raises it: the queue keeps working and the entry sits for months.
        return quarantined > 0
            ? new DoctorCheck(
                "queue",
                DoctorStatus.Warning,
                CliText.QueueQuarantined(queued, quarantined),
                QueueFacts(queued, quarantined, stalled))
            : new DoctorCheck(
                "queue",
                DoctorStatus.Ok,
                CliText.QueueClean(queued),
                QueueFacts(queued, quarantined, stalled));

        static int Count(string path) =>
            Directory.Exists(path)
                ? Directory.EnumerateFileSystemEntries(path).Count()
                : 0;

        // How long the oldest job that has never been attempted has been waiting, or null when
        // every job has had its turn. Unreadable state files are skipped rather than reported:
        // that is what the quarantine is for, and a diagnostic that throws while diagnosing is
        // worse than one that says a little less.
        static TimeSpan? OldestUnattempted(string path)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            TimeSpan? oldest = null;
            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(
                        File.ReadAllText(Path.Combine(directory, "state.json")));
                    var state = document.RootElement;
                    if (state.GetProperty("State").GetString() != "Queued" ||
                        state.GetProperty("AttemptCount").GetInt32() != 0)
                    {
                        continue;
                    }

                    var waited = DateTimeOffset.UtcNow -
                        state.GetProperty("CreatedAtUtc").GetDateTimeOffset();
                    if (oldest is null || waited > oldest)
                    {
                        oldest = waited;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException
                        or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                }
            }

            return oldest;
        }
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
            "policy.models",
            RuntimeDirectories.SettingsFile(root, BrokerPolicy.FileName),
            () => new ModelResidencyPolicyStore(root).Read(),
            policy => CliText.PolicyModels(
                policy.ModelResidency,
                policy.IdleModelKeepAliveSeconds),
            (policy, found) => Facts(
                ("modelResidency", policy.ModelResidency.ToString()),
                ("keepAliveSeconds", policy.IdleModelKeepAliveSeconds),
                ("fileFound", found)));

        yield return PolicyCheck(
            "policy.retention",
            RuntimeDirectories.SettingsFile(root, RuntimeRetentionPolicy.FileName),
            () => new RuntimeRetentionPolicyStore(root).Read(),
            policy => CliText.PolicyRetention(
                policy.GenerationsPerRepository,
                policy.InstalledVersions,
                policy.TelemetryRetentionDays),
            (policy, found) => Facts(
                ("generationsPerRepository", policy.GenerationsPerRepository),
                ("installedVersions", policy.InstalledVersions),
                ("telemetryRetentionDays", policy.TelemetryRetentionDays),
                ("fileFound", found)));

        yield return PolicyCheck(
            "policy.languageServers",
            RuntimeDirectories.SettingsFile(root, LanguageServerPolicy.FileName),
            () => new LanguageServerPolicyStore(root).Read(),
            policy => policy.Enabled
                ? CliText.PolicyLanguageServersEnabled(string.Join(
                    ", ",
                    policy.Languages.Where(l => l.Value.Enabled).Select(l => l.Key)))
                : CliText.PolicyLanguageServersDisabled,
            (policy, found) => Facts(
                ("enabled", policy.Enabled),
                ("languages", policy.Languages
                    .Where(language => language.Value.Enabled)
                    .Select(language => language.Key)
                    .ToArray()),
                ("fileFound", found)));

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
                CliText.UpdateCheckDisabled,
                Facts(("enabled", false)));
        }

        var state = new UpdateCheckStateStore(root).Read();
        var installed = InstalledVersionReader.Read(root);
        var availability = UpdateComparison.Compare(state, installed);
        var updateFacts = Facts(
            ("enabled", true),
            ("availability", availability.ToString()),
            ("latestVersion", state.LatestVersion),
            ("releaseUrl", state.ReleaseUrl),
            ("checkedAtUtc", state.CheckedAtUtc?.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        return availability switch
        {
            UpdateAvailability.Available => new DoctorCheck(
                "update",
                DoctorStatus.Warning,
                CliText.UpdateAvailable(
                    state.LatestVersion,
                    installed.DisplayName,
                    state.ReleaseUrl),
                updateFacts),
            UpdateAvailability.UpToDate => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateUpToDate(
                    installed.DisplayName,
                    state.CheckedAtUtc?.ToString(
                        "yyyy-MM-dd HH:mm",
                        CultureInfo.InvariantCulture)),
                updateFacts),
            _ when state.Status == UpdateCheckStatus.Unavailable => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateUnknownUnavailable(state.CheckedAtUtc?.ToString(
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture)),
                updateFacts),
            // Verified, but nothing here can be compared against it: an installation made
            // before the release version was recorded knows only its directory name. Said
            // plainly, because answering "up to date" from a comparison that failed is the
            // defect this check was rewritten for.
            _ when state.Status == UpdateCheckStatus.Verified => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateIncomparable(state.LatestVersion),
                updateFacts),
            _ => new DoctorCheck(
                "update",
                DoctorStatus.Ok,
                CliText.UpdateNeverChecked,
                updateFacts),
        };
    }

    private static DoctorCheck PolicyCheck<T>(
        string name,
        string path,
        Func<T> read,
        Func<T, string> describe,
        Func<T, bool, IReadOnlyList<DoctorFact>> facts)
    {
        try
        {
            var policy = read();
            var found = File.Exists(path);
            return new DoctorCheck(
                name,
                DoctorStatus.Ok,
                found ? describe(policy) : CliText.PolicyDefaults(describe(policy)),
                facts(policy, found));
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
                    CliText.RepositoryNotConnected(identity.RepositoryRoot.Value),
                    Facts(
                        ("repositoryId", identity.RepositoryId),
                        ("repositoryRoot", identity.RepositoryRoot.Value),
                        ("state", nameof(RepositoryIndexState.NotConfigured))));
            }

            var repositoryFacts = Facts(
                ("repositoryId", identity.RepositoryId),
                ("repositoryRoot", identity.RepositoryRoot.Value),
                ("state", manifest.State.ToString()),
                ("generationId", manifest.CurrentGenerationId));
            return new DoctorCheck(
                "repository",
                manifest.State == RepositoryIndexState.Current
                    ? DoctorStatus.Ok
                    : DoctorStatus.Warning,
                CliText.RepositoryState(manifest.State, Short(manifest.CurrentGenerationId)),
                repositoryFacts);
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

    /// <summary>
    /// The facts a check established, in the order they are published. A pair whose value is
    /// null is dropped rather than written: absent says the check could not establish it, and
    /// a null would make a caller test for two things that mean the same.
    /// </summary>
    private static IReadOnlyList<DoctorFact> Facts(
        params (string Name, object? Value)[] facts) =>
        facts
            .Where(fact => fact.Value is not null)
            .Select(fact => new DoctorFact(fact.Name, fact.Value))
            .ToArray();

    private static IReadOnlyList<DoctorFact> BrokerFacts(
        BrokerProcessState state,
        TimeSpan silence) =>
        Facts(
            ("processId", state.ProcessId),
            ("heartbeatAgeSeconds", (int)silence.TotalSeconds));

    private static IReadOnlyList<DoctorFact> QueueFacts(
        int queued,
        int quarantined,
        TimeSpan? oldestUnattempted) =>
        Facts(
            ("queued", queued),
            ("quarantined", quarantined),
            ("oldestUnattemptedMinutes", oldestUnattempted is { } age
                ? (int)age.TotalMinutes
                : null));

    /// <summary>
    /// Reads the arguments of <c>doctor</c>, refusing anything it does not understand.
    ///
    /// It used to find <c>--root</c> with an index search and ignore the rest, so a typo and a
    /// deliberate omission produced the same report: one with no repository check and nothing
    /// saying why. Under <c>--json</c> that would have been the same envelope, and a caller
    /// cannot see the difference between a check that was not asked for and one it misspelled.
    /// </summary>
    public static bool TryParseArguments(
        IReadOnlyList<string> arguments,
        out string? repositoryRoot,
        out CommandRefusal? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        repositoryRoot = null;
        refusal = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--root")
            {
                if (index + 1 >= arguments.Count)
                {
                    refusal = new CommandRefusal(
                        "root_value_missing",
                        CliText.DoctorRootWithoutDirectory);
                    return false;
                }

                if (repositoryRoot is not null)
                {
                    refusal = new CommandRefusal(
                        "repository_ambiguous",
                        CliText.DoctorTwoRepositories);
                    return false;
                }

                repositoryRoot = arguments[++index];
                continue;
            }

            refusal = new CommandRefusal(
                "argument_unknown",
                CliText.DoctorUnknownArgument(argument, CliUsage.Doctor));
            return false;
        }

        return true;
    }

    /// <summary>
    /// The report as a program reads it.
    ///
    /// The verdict is inside the answer rather than in the envelope's <c>ok</c>: this command
    /// exits 1 when a check failed, which is a verdict about the machine and not about the run,
    /// and an envelope that dropped its data there would deny a caller the report in exactly the
    /// case it asked for it.
    /// </summary>
    public static JsonObject Describe(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var checks = new JsonArray();
        foreach (var check in report.Checks)
        {
            var described = new JsonObject
            {
                ["name"] = check.Name,
                ["status"] = check.Status.ToString(),
                ["detail"] = check.Detail,
            };
            foreach (var fact in check.Facts ?? [])
            {
                described[fact.Name] = Value(fact.Value);
            }

            checks.Add(described);
        }

        return new JsonObject
        {
            ["verdict"] = report.Verdict.ToString(),
            ["failed"] = report.Checks.Count(check => check.Status == DoctorStatus.Failed),
            ["warned"] = report.Checks.Count(check => check.Status == DoctorStatus.Warning),
            ["checks"] = checks,
        };

        static JsonNode? Value(object? value) => value switch
        {
            null => null,
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            IEnumerable<string> values => new JsonArray(
                values.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            _ => JsonValue.Create(value.ToString()),
        };
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
