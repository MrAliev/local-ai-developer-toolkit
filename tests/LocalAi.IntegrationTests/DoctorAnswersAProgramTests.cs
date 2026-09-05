using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The command anyone verifying an installation runs, and the one a scheduled task would run on
/// a timer, was the last state-reporting command a program could not read: its answer changed
/// language with the machine it ran on, and `capabilities` named it while offering nothing to
/// parse (#355).
/// </summary>
public sealed class DoctorAnswersAProgramTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-doctor-json-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_envelope_carries_the_report()
    {
        Install("v1");

        using var document = JsonDocument.Parse(
            MachineOutput.Answer("doctor", DoctorCommand.Describe(DoctorCommand.Inspect(_root))));

        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal("doctor", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var data = root.GetProperty("data");
        // Warning rather than Ok, and deterministically so: nothing has started a broker under
        // this runtime root, and a broker that is not running is a note rather than a fault.
        Assert.Equal("Warning", data.GetProperty("verdict").GetString());
        Assert.Equal(0, data.GetProperty("failed").GetInt32());
        Assert.Equal(1, data.GetProperty("warned").GetInt32());
        Assert.NotEmpty(data.GetProperty("checks").EnumerateArray());
    }

    /// <summary>
    /// The case the shape exists for. A caller asking whether this installation is healthy must
    /// get the answer when it is bad news — which is what an envelope with no data would deny it.
    /// </summary>
    [Fact]
    public void A_failed_check_is_data_rather_than_an_error()
    {
        Directory.CreateDirectory(_root);
        var report = DoctorCommand.Inspect(_root);

        using var document = JsonDocument.Parse(
            MachineOutput.Answer("doctor", DoctorCommand.Describe(report)));

        var root = document.RootElement;
        Assert.Equal(1, report.ExitCode);
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(root.TryGetProperty("error", out _));
        var data = root.GetProperty("data");
        Assert.Equal("Failed", data.GetProperty("verdict").GetString());
        Assert.True(data.GetProperty("failed").GetInt32() >= 1);
        Assert.Contains(
            data.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "version" &&
                     check.GetProperty("status").GetString() == "Failed");
    }

    /// <summary>
    /// A number the prose states inside a sentence is a number a caller would otherwise have to
    /// parse out of prose it was told never to parse.
    /// </summary>
    [Fact]
    public void What_a_check_established_is_a_field_of_its_own()
    {
        Install("v1");

        var checks = Checks(DoctorCommand.Inspect(_root));

        var queue = checks.Single(check => check.GetProperty("name").GetString() == "queue");
        Assert.Equal(0, queue.GetProperty("queued").GetInt32());
        Assert.Equal(0, queue.GetProperty("quarantined").GetInt32());
        var version = checks.Single(check => check.GetProperty("name").GetString() == "version");
        Assert.Equal("v1", version.GetProperty("versionDirectory").GetString());
        var models = checks.Single(
            check => check.GetProperty("name").GetString() == "policy.models");
        Assert.Equal("RequireFullVram", models.GetProperty("modelResidency").GetString());
        Assert.False(models.GetProperty("fileFound").GetBoolean());
    }

    /// <summary>
    /// A fact a check could not establish is absent, never null: absent says "not established",
    /// and null would make a caller test for two things.
    /// </summary>
    [Fact]
    public void A_fact_a_check_could_not_establish_is_absent()
    {
        Install("v1");

        var broker = Checks(DoctorCommand.Inspect(_root))
            .Single(check => check.GetProperty("name").GetString() == "broker");

        Assert.Equal("Warning", broker.GetProperty("status").GetString());
        Assert.False(broker.TryGetProperty("processId", out _));
    }

    /// <summary>
    /// Three names carried a colon and a space, which a JSON key would have kept forever. One
    /// name per check, in both faces, so the console and a plugin say the same thing.
    /// </summary>
    [Fact]
    public void No_check_is_named_with_a_space()
    {
        Install("v1");

        var report = DoctorCommand.Inspect(_root);

        Assert.All(report.Checks, check =>
            Assert.DoesNotContain(" ", check.Name, StringComparison.Ordinal));
        Assert.Contains(report.Checks, check => check.Name == "policy.models");
        Assert.Contains(report.Checks, check => check.Name == "policy.languageServers");
    }

    /// <summary>
    /// A typo and a deliberate omission produced the same report, and would have produced the
    /// same envelope: neither carried a repository check, and nothing said why.
    /// </summary>
    [Theory]
    [InlineData("argument_unknown", "--rooot", "R:\repo")]
    [InlineData("root_value_missing", "--root")]
    [InlineData("repository_ambiguous", "--root", @"R:\one", "--root", "R:\two")]
    public void An_argument_it_does_not_understand_is_refused(string code, params string[] args)
    {
        Assert.False(DoctorCommand.TryParseArguments(args, out _, out var refusal));

        Assert.NotNull(refusal);
        Assert.Equal(code, refusal.Code);
    }

    [Fact]
    public void The_repository_it_was_given_is_what_it_checks()
    {
        Assert.True(DoctorCommand.TryParseArguments(
            ["--root", "R:\repo"],
            out var repositoryRoot,
            out _));

        Assert.Equal("R:\repo", repositoryRoot);
    }

    [Fact]
    public void The_usage_line_marks_the_flag_and_the_listing_names_the_command()
    {
        Assert.Contains("[--json]", CliUsage.Doctor, StringComparison.Ordinal);
        Assert.Contains("doctor", MachineOutput.Commands, StringComparer.Ordinal);
    }

    private static JsonElement[] Checks(DoctorReport report)
    {
        using var document = JsonDocument.Parse(
            MachineOutput.Answer("doctor", DoctorCommand.Describe(report)));
        return document.RootElement
            .GetProperty("data")
            .GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.Clone())
            .ToArray();
    }

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
