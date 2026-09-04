using System.Globalization;
using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The value of a diagnostic is what it says when something is wrong, so these break an
/// installation one way at a time and assert the report names that way and no other.
/// </summary>
public sealed class DoctorCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-doctor-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_complete_installation_reports_no_problems()
    {
        Install("v1");

        var report = DoctorCommand.Inspect(_root);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(DoctorStatus.Ok, Check(report, "version").Status);
        Assert.Equal(DoctorStatus.Ok, Check(report, "launcher").Status);
        Assert.Contains("v1", Check(report, "version").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_runtime_root_fails_rather_than_reporting_nothing()
    {
        Directory.CreateDirectory(_root);

        var report = DoctorCommand.Inspect(_root);

        Assert.Equal(1, report.ExitCode);
        Assert.Equal(DoctorStatus.Failed, Check(report, "version").Status);
    }

    /// <summary>
    /// The shape a half-finished install leaves behind: the pointer is confident and the version
    /// it names is incomplete. Every tool then fails one at a time, each blaming itself.
    /// </summary>
    [Fact]
    public void A_version_missing_binaries_is_named_along_with_what_is_missing()
    {
        Install("v1");
        File.Delete(Path.Combine(_root, "bin", "versions", "v1", "codesearch-mcp.exe"));

        var check = Check(DoctorCommand.Inspect(_root), "version");

        Assert.Equal(DoctorStatus.Failed, check.Status);
        Assert.Contains("codesearch-mcp.exe", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pointer_naming_an_absent_version_is_a_failure()
    {
        Install("v1");
        Directory.Delete(Path.Combine(_root, "bin", "versions", "v1"), recursive: true);

        var check = Check(DoctorCommand.Inspect(_root), "version");

        Assert.Equal(DoctorStatus.Failed, check.Status);
        Assert.Contains("v1", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stopped broker is not a fault — it starts on demand — so it must not make the exit code
    /// non-zero. A diagnostic that fails for ordinary states gets its exit code ignored.
    /// </summary>
    [Fact]
    public void A_broker_that_is_not_running_is_worth_reading_but_not_a_failure()
    {
        Install("v1");

        var report = DoctorCommand.Inspect(_root);

        Assert.Equal(DoctorStatus.Warning, Check(report, "broker").Status);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void A_broker_silent_for_too_long_is_distinguished_from_a_live_one()
    {
        Install("v1");
        WriteHost(DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

        var stale = Check(DoctorCommand.Inspect(_root), "broker");
        WriteHost(DateTimeOffset.UtcNow);
        var live = Check(DoctorCommand.Inspect(_root), "broker");

        Assert.Equal(DoctorStatus.Warning, stale.Status);
        Assert.Equal(DoctorStatus.Ok, live.Status);
    }

    /// <summary>
    /// Quarantined jobs are never retried and nothing else mentions them, so they accumulate
    /// unseen — two sat in this runtime for six weeks before anyone looked.
    /// </summary>
    [Fact]
    public void Quarantined_jobs_are_surfaced()
    {
        Install("v1");
        Directory.CreateDirectory(Path.Combine(_root, "quarantine", "stuck-job"));

        var check = Check(DoctorCommand.Inspect(_root), "queue");

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Contains("quarantined", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A queue that has stopped looks exactly like a busy one to a count of directories, and that
    /// is what this check was: `4 queued` and `No problems`, printed for two hours while a Git
    /// hook's embedding job waited behind a job nothing would ever pick (#335). The broker was
    /// healthy by every measure it reports — a heartbeat one second old, the backend reachable —
    /// because it was the queue that was not moving.
    ///
    /// The evidence was already on disk: each job's state file carries when it was created and
    /// how many times it has been attempted. Never attempted, and older than the age at which the
    /// scheduler itself force-includes a starved job, is not a queue anybody should call healthy.
    /// </summary>
    [Fact]
    public void A_queue_that_has_not_moved_is_not_reported_as_healthy()
    {
        Install("v1");
        Queued("stalled-job", DateTimeOffset.UtcNow.AddHours(-2), attempts: 0);

        var check = Check(DoctorCommand.Inspect(_root), "queue");

        Assert.Equal(DoctorStatus.Warning, check.Status);
    }

    /// <summary>
    /// A job merely waiting its turn is not a problem. Something is always last in a queue, and a
    /// check that warned about that would be noise on every busy machine.
    /// </summary>
    [Fact]
    public void A_job_waiting_its_turn_is_not_a_problem()
    {
        Install("v1");
        Queued("fresh-job", DateTimeOffset.UtcNow, attempts: 0);

        var check = Check(DoctorCommand.Inspect(_root), "queue");

        Assert.Equal(DoctorStatus.Ok, check.Status);
    }

    /// <summary>
    /// A job that has been attempted is being served, however long it has been there: a long
    /// inference is work, not a stall.
    /// </summary>
    [Fact]
    public void A_long_job_that_is_being_attempted_is_not_a_stall()
    {
        Install("v1");
        Queued("running-job", DateTimeOffset.UtcNow.AddHours(-2), attempts: 1);

        var check = Check(DoctorCommand.Inspect(_root), "queue");

        Assert.Equal(DoctorStatus.Ok, check.Status);
    }

    /// <summary>One job directory, shaped the way the durable queue writes them.</summary>
    private void Queued(string name, DateTimeOffset createdAtUtc, int attempts)
    {
        var directory = Path.Combine(_root, "jobs", name);
        Directory.CreateDirectory(directory);
        var moment = createdAtUtc.ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(
            Path.Combine(directory, "state.json"),
            $$"""
            {"SchemaVersion":1,"JobId":"{{Guid.NewGuid()}}","Sequence":1,
             "Priority":"Foreground","State":"Queued",
             "CreatedAtUtc":"{{moment}}","UpdatedAtUtc":"{{moment}}",
             "WorkerId":null,"LeaseId":null,"LeaseExpiresAtUtc":null,"HeartbeatAtUtc":null,
             "AttemptCount":{{attempts}},"RecoveryCount":0,"FailureCode":null}
            """);
    }

    /// <summary>
    /// A policy file holding exactly the default values is configured, not broken. The first
    /// version of this check inferred "malformed" from "reads back as the defaults" and cried
    /// wolf on the first real installation it saw.
    /// </summary>
    [Fact]
    public void A_policy_file_that_matches_the_defaults_is_not_reported_as_a_problem()
    {
        Install("v1");
        new ModelResidencyPolicyStore(_root).Write(BrokerPolicy.Default);

        var report = DoctorCommand.Inspect(_root);

        Assert.Equal(DoctorStatus.Ok, Check(report, "policy: models").Status);
        Assert.Equal(0, report.ExitCode);
    }

    private static DoctorCheck Check(DoctorReport report, string name) =>
        report.Checks.Single(check => check.Name == name);

    private void WriteHost(DateTimeOffset heartbeat) =>
        File.WriteAllText(
            Path.Combine(_root, "host.json"),
            JsonSerializer.Serialize(
                new BrokerProcessState(
                    4242,
                    heartbeat - TimeSpan.FromMinutes(1),
                    heartbeat,
                    1,
                    Path.Combine(_root, "bin", "versions", "v1", "LocalAi.Broker.exe")),
                LocalAiJson.Strict));

    private void Install(string version)
    {
        var versionDirectory = Path.Combine(_root, "bin", "versions", version);
        Directory.CreateDirectory(versionDirectory);
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            File.WriteAllText(Path.Combine(versionDirectory, file), file);
        }

        var launcherDirectory = Path.Combine(_root, "bin", "launcher");
        Directory.CreateDirectory(launcherDirectory);
        File.WriteAllText(
            Path.Combine(launcherDirectory, LocalAiPackageLayout.StableLauncherFile),
            "launcher");
        File.WriteAllText(
            Path.Combine(_root, "bin", "current.json"),
            $$"""{"schemaVersion":1,"version":"{{version}}"}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
