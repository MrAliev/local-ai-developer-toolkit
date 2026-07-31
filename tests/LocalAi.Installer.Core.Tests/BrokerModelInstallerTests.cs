using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

public sealed class BrokerModelInstallerTests
{
    private static readonly DateTimeOffset VerifiedUtc =
        new(2026, 7, 31, 8, 9, 10, TimeSpan.Zero);
    private readonly InstallationLayout layout =
        InstallationLayout.FromLocalAppData(@"C:\LocalAppData");

    [Fact]
    public async Task Installed_model_skips_pull_but_is_always_preflighted_exactly()
    {
        var runner = new RecordingProcessRunner(
            Success(Status(["qwen3.5:9b"], [], "signed-7")),
            Success(Preflight("qwen3.5:9b", 8192, 100, 100, true)));
        var launcher = Launcher();
        var installer = Installer(runner, launcher);

        var result = await installer.InstallAsync(
            [Request("a", "qwen3.5:9b", 8192, "signed-7")],
            TestContext.Current.CancellationToken);

        var model = Assert.Single(result.Models);
        Assert.Equal(BrokerModelInstallOutcome.Accepted, model.Outcome);
        Assert.False(model.PullAttempted);
        Assert.False(model.PullCompleted);
        Assert.False(result.ExternalStateMayBeIndeterminate);
        Assert.Equal(
            [
                new[] { "run", "localai", "model", "status" },
                new[] { "run", "localai", "model", "preflight", "--model", "qwen3.5:9b", "--context", "8192" },
            ],
            runner.Calls.Select(call => call.Arguments));
        Assert.All(runner.Calls, call => Assert.Equal(layout.LauncherPath, call.Executable));
        Assert.All(runner.Calls, call => Assert.Equal(TimeSpan.FromMinutes(5), call.Timeout));
        Assert.Equal(4, launcher.RevalidationCount);
    }

    [Fact]
    public async Task Missing_models_pull_with_exact_catalog_then_preflight_sequentially()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            Success(Pull("model-a", "signed-7")),
            Success(Preflight("model-a", 2048, 80, 80, true)),
            Success(Pull("model-b", "signed-7")),
            Success(Preflight("model-b", 4096, 90, 90, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [
                Request("a", "model-a", 2048, "signed-7"),
                Request("b", "model-b", 4096, "signed-7"),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Models.Count);
        Assert.All(result.Models, model => Assert.True(model.PullCompleted));
        Assert.Equal(
            [
                "run localai model status",
                "run localai model pull --model model-a --catalog-version signed-7",
                "run localai model preflight --model model-a --context 2048",
                "run localai model pull --model model-b --catalog-version signed-7",
                "run localai model preflight --model model-b --context 4096",
            ],
            runner.Calls.Select(call => string.Join(' ', call.Arguments)));
    }

    [Fact]
    public async Task Pending_model_skips_duplicate_pull_but_is_preflighted()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], ["model-a"], "signed-7")),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelInstallOutcome.Accepted, Assert.Single(result.Models).Outcome);
        Assert.DoesNotContain(
            runner.Calls,
            call => call.Arguments.Contains("pull", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, true, "a", "model-a", "signed-7")]
    [InlineData(true, false, "a", "model-a", "signed-7")]
    [InlineData(true, true, "", "model-a", "signed-7")]
    [InlineData(true, true, "a", "../unsafe", "signed-7")]
    [InlineData(true, true, "a", "model-a", "../unsafe")]
    public async Task Invalid_or_unconsented_request_runs_no_command(
        bool selected,
        bool consent,
        string actionId,
        string model,
        string catalogVersion)
    {
        var runner = new RecordingProcessRunner();
        var installer = Installer(runner, Launcher());
        var request = Request(
            actionId, model, 2048, catalogVersion, selected, consent);

        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(
            [request], TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Duplicate_action_or_model_context_runs_no_command()
    {
        var runner = new RecordingProcessRunner();
        var installer = Installer(runner, Launcher());

        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(
            [
                Request("same", "model-a", 2048, "signed-7"),
                Request("same", "model-b", 4096, "signed-7"),
            ],
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(
            [
                Request("a", "model-a", 2048, "signed-7"),
                Request("b", "model-a", 2048, "signed-7"),
            ],
            TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Residency_mismatch_is_rejected_and_offers_only_ordered_safe_fallbacks()
    {
        var choices = new[]
        {
            Choice("model-a", 2048, 70, 100, enabled: true),
            Choice("model-b", 2048, 60, 100, enabled: true),
            Choice("model-a", 8192, 100, 100, enabled: true),
            Choice("model-c", 2048, 120, 100, enabled: false),
            Choice("model-d", 2048, 110, 100, enabled: true),
        };
        var runner = new RecordingProcessRunner(
            Success(Status(["model-a"], [], "signed-7")),
            Success(Preflight("model-a", 8192, 100, 99, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 8192, "signed-7", choices: choices)],
            TestContext.Current.CancellationToken);

        var model = Assert.Single(result.Models);
        Assert.Equal(BrokerModelInstallOutcome.RejectedResidency, model.Outcome);
        Assert.Equal(
            [("model-a", 2048), ("model-b", 2048)],
            model.FallbackSuggestions.Select(choice => (choice.Model, choice.ContextTokens)));
        Assert.False(model.NoFallbackAvailable);
    }

    [Fact]
    public async Task Broker_rejection_is_rejected_residency_with_explicit_no_fallback()
    {
        var runner = new RecordingProcessRunner(
            Success(Status(["model-a"], [], "signed-7")),
            new ProcessResult(
                3,
                Json(new ModelPreflightCommandRejected(
                    1, "preflight", false, "model-a", 2048, "residency_rejected")),
                string.Empty,
                false,
                false));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        var model = Assert.Single(result.Models);
        Assert.Equal(BrokerModelInstallOutcome.RejectedResidency, model.Outcome);
        Assert.Empty(model.FallbackSuggestions);
        Assert.True(model.NoFallbackAvailable);
    }

    public static TheoryData<string> InvalidStatusJson => new()
    {
        "{ malformed",
        "{\"schemaVersion\":1,\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":true,\"catalogVersion\":\"signed-7\",\"installedModels\":[],\"pendingPullModels\":[]}",
        "{\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":true,\"catalogVersion\":\"signed-7\",\"installedModels\":[],\"pendingPullModels\":[],\"unknown\":true}",
        "{\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":true,\"catalogVersion\":\"signed-7\",\"installedModels\":[],\"pendingPullModels\":[]} trailing",
        "{\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":true,\"catalogVersion\":\"other\",\"installedModels\":[],\"pendingPullModels\":[]}",
    };

    [Theory]
    [MemberData(nameof(InvalidStatusJson))]
    public async Task Strict_status_response_attacks_stop_before_pull(string stdout)
    {
        var runner = new RecordingProcessRunner(
            new ProcessResult(0, stdout, string.Empty, false, false));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Models);
        Assert.Equal(BrokerModelBatchStopReason.ProtocolFailure, result.StopReason);
        Assert.False(result.ExternalStateMayBeIndeterminate);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Stderr_or_truncated_output_is_never_accepted()
    {
        var runner = new RecordingProcessRunner(
            new ProcessResult(
                0,
                Status([], [], "signed-7"),
                "raw secret",
                false,
                false,
                StandardOutputTruncated: false,
                StandardErrorTruncated: false));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.ProtocolFailure, result.StopReason);
        Assert.DoesNotContain("secret", result.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_during_pull_stops_later_models_and_marks_state_indeterminate()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            new ProcessResult(null, string.Empty, string.Empty, false, true));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [
                Request("a", "model-a", 2048, "signed-7"),
                Request("b", "model-b", 2048, "signed-7"),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.Cancelled, result.StopReason);
        Assert.True(result.ExternalStateMayBeIndeterminate);
        Assert.Equal(
            BrokerModelInstallOutcome.Cancelled,
            Assert.Single(result.Models).Outcome);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("nonzero")]
    [InlineData("truncated")]
    [InlineData("malformed")]
    public async Task Pull_process_failures_stop_and_mark_state_indeterminate(string failure)
    {
        var pull = failure switch
        {
            "timeout" => new ProcessResult(null, string.Empty, string.Empty, true, false),
            "nonzero" => new ProcessResult(
                1,
                Json(new ModelCommandError(1, "pull", false, "broker_failure")),
                string.Empty,
                false,
                false),
            "truncated" => new ProcessResult(
                0,
                Pull("model-a", "signed-7"),
                string.Empty,
                false,
                false,
                StandardOutputTruncated: true),
            "malformed" => new ProcessResult(
                0,
                "{ malformed",
                string.Empty,
                false,
                false),
            _ => throw new InvalidOperationException(),
        };
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")), pull);
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.True(result.ExternalStateMayBeIndeterminate);
        Assert.NotEqual(BrokerModelBatchStopReason.None, result.StopReason);
    }

    [Fact]
    public async Task Mismatched_preflight_model_context_or_non_utc_timestamp_is_protocol_failure()
    {
        var runner = new RecordingProcessRunner(
            Success(Status(["model-a"], [], "signed-7")),
            Success(Json(new ModelPreflightCommandSuccess(
                1,
                "preflight",
                true,
                "model-b",
                4096,
                100,
                100,
                true,
                new DateTimeOffset(2026, 7, 31, 8, 9, 10, TimeSpan.FromHours(3))))));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.ProtocolFailure, result.StopReason);
        Assert.Equal(
            BrokerModelInstallOutcome.Failed,
            Assert.Single(result.Models).Outcome);
    }

    [Theory]
    [InlineData(ProcessTerminationCause.Timeout, BrokerModelBatchStopReason.TimedOut)]
    [InlineData(ProcessTerminationCause.Cancellation, BrokerModelBatchStopReason.Cancelled)]
    public async Task Unconfirmed_process_termination_stops_later_actions_and_is_indeterminate(
        ProcessTerminationCause cause,
        BrokerModelBatchStopReason expectedStop)
    {
        var runner = new TerminatingProcessRunner(
            Success(Status([], [], "signed-7")),
            new ProcessTerminationException(42, cause, "raw termination detail"));
        var launcher = Launcher();
        var installer = new BrokerModelInstaller(
            runner,
            launcher,
            layout,
            TimeSpan.FromMinutes(5));

        var result = await installer.InstallAsync(
            [
                Request("a", "model-a", 2048, "signed-7"),
                Request("b", "model-b", 2048, "signed-7"),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStop, result.StopReason);
        Assert.True(result.ExternalStateMayBeIndeterminate);
        Assert.DoesNotContain("raw", result.Code, StringComparison.Ordinal);
        Assert.Equal(2, runner.CallCount);
        Assert.Equal(4, launcher.RevalidationCount);
    }

    [Fact]
    public async Task Launcher_path_must_be_the_exact_canonical_layout_path()
    {
        var runner = new RecordingProcessRunner();
        var wrongLauncher = new RecordingLauncher(@"C:\other\localai-launcher.exe");

        Assert.Throws<ArgumentException>(() => Installer(runner, wrongLauncher));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Launcher_path_must_already_be_canonical()
    {
        var runner = new RecordingProcessRunner();
        var nonCanonical = new RecordingLauncher(
            @"C:\LocalAppData\LocalAi\bin\launcher\..\launcher\localai-launcher.exe");

        Assert.Throws<ArgumentException>(() => Installer(runner, nonCanonical));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Command_timeout_is_positive_and_bounded()
    {
        var runner = new RecordingProcessRunner();

        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerModelInstaller(
            runner, Launcher(), layout, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerModelInstaller(
            runner, Launcher(), layout, TimeSpan.FromMinutes(31)));
        Assert.Empty(runner.Calls);
    }

    private BrokerModelInstaller Installer(
        RecordingProcessRunner runner,
        RecordingLauncher launcher) =>
        new(runner, launcher, layout, TimeSpan.FromMinutes(5));

    private RecordingLauncher Launcher() => new(layout.LauncherPath);

    private static BrokerModelInstallRequest Request(
        string actionId,
        string model,
        int context,
        string catalogVersion,
        bool selected = true,
        bool consent = true,
        IReadOnlyList<ModelRecommendationChoice>? choices = null) =>
        new(
            new ModelInstallAction(actionId, model, context, selected, consent),
            catalogVersion,
            choices ?? [Choice(model, context, 100, 100, enabled: true)]);

    private static ModelRecommendationChoice Choice(
        string model,
        int context,
        ulong required,
        ulong available,
        bool enabled) =>
        new(
            model,
            context,
            required,
            0,
            0,
            required,
            available,
            available >= required ? available - required : 0,
            available >= required ? 0 : required - available,
            enabled,
            "test choice");

    private static string Status(
        IReadOnlyList<string> installed,
        IReadOnlyList<string> pending,
        string catalogVersion) =>
        Json(new ModelStatusCommandSuccess(
            1, "status", true, catalogVersion, installed, pending));

    private static string Pull(string model, string catalogVersion) =>
        Json(new ModelPullCommandSuccess(
            1, "pull", true, model, catalogVersion, "success"));

    private static string Preflight(
        string model,
        int context,
        long size,
        long sizeVram,
        bool fullyResident) =>
        Json(new ModelPreflightCommandSuccess(
            1,
            "preflight",
            true,
            model,
            context,
            size,
            sizeVram,
            fullyResident,
            VerifiedUtc));

    private static string Json<T>(T value) =>
        JsonSerializer.Serialize(value, LocalAiJson.Strict) + Environment.NewLine;

    private static ProcessResult Success(string stdout) =>
        new(0, stdout, string.Empty, false, false);

    private sealed class RecordingLauncher(string canonicalPath) : ITrustedStableLauncher
    {
        public string CanonicalPath { get; } = canonicalPath;
        public int RevalidationCount { get; private set; }
        public void Revalidate() => RevalidationCount++;
    }

    private sealed class RecordingProcessRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> results = new(results);
        public List<Call> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new Call(executable, arguments.ToArray(), timeout));
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed record Call(
        string Executable,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout);

    private sealed class TerminatingProcessRunner(
        ProcessResult first,
        ProcessTerminationException failure) : IProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return CallCount == 1
                ? Task.FromResult(first)
                : Task.FromException<ProcessResult>(failure);
        }
    }
}
