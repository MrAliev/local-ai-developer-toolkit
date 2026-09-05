using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Planning;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class BrokerModelInstallerTests : IDisposable
{
    private static readonly DateTimeOffset VerifiedUtc =
        new(2026, 7, 31, 8, 9, 10, TimeSpan.Zero);

    /// <summary>
    /// How long the activation gate is given while a test blocks it. The cancellation test
    /// asserts against a fraction of this rather than against an absolute number of seconds, so
    /// the two are one relationship: finishing well inside the budget means the cancellation was
    /// observed, and finishing at the budget means it was not. A minute apart, no plausible
    /// scheduling delay makes one look like the other.
    /// </summary>
    private static readonly TimeSpan GateBudget = TimeSpan.FromSeconds(60);
    private readonly InstallationLayout layout =
        InstallationLayout.FromLocalAppData(@"C:\LocalAppData");
    private readonly List<string> activationRoots = [];

    [Fact]
    public void Public_api_requires_task_six_lease_and_live_verified_package()
    {
        Assert.False(typeof(ITrustedStableLauncher).IsPublic);

        var constructor = Assert.Single(typeof(BrokerModelInstaller).GetConstructors());
        Assert.Equal(
            [
                typeof(IProcessRunner),
                typeof(InstallationLayoutLease),
                typeof(VerifiedPackage),
                typeof(TimeSpan),
                // The pull's own ceiling, optional so every existing caller keeps compiling.
                typeof(TimeSpan?),
            ],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(ITrustedStableLauncher),
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(InstallationLayoutLease.TrustedLauncher),
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }

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
                new[] { "run", "localai", "model", "preflight", "--model", "qwen3.5:9b", "--context", "8192", "--catalog-version", "signed-7" },
            ],
            runner.Calls.Select(call => call.Arguments));
        Assert.All(runner.Calls, call => Assert.Equal(layout.LauncherPath, call.Executable));
        Assert.All(runner.Calls, call => Assert.Equal(TimeSpan.FromMinutes(5), call.Timeout));
        Assert.Equal(4, launcher.RevalidationCount);
    }

    /// <summary>
    /// A preflight loads a model into video memory and a pull is measured in gigabytes, so a
    /// batch runs for minutes. Reporting only at the end left the progress bar frozen on the
    /// previous phase, which reads as a hung installer with nothing to press but Cancel.
    /// </summary>
    [Fact]
    public async Task Each_model_is_reported_before_the_wait_it_causes()
    {
        var runner = new RecordingProcessRunner(
            Success(Status(["model-b"], [], "signed-7")),
            Success(Pull("model-a", "signed-7")),
            Success(Preflight("model-a", 2048, 80, 80, true)),
            Success(Preflight("model-b", 4096, 90, 90, true)));
        var installer = Installer(runner, Launcher());
        var steps = new List<ModelProvisioningProgress>();

        await installer.InstallAsync(
            [
                Request("a", "model-a", 2048, "signed-7"),
                Request("b", "model-b", 4096, "signed-7"),
            ],
            new ImmediateProgress<ModelProvisioningProgress>(steps.Add),
            TestContext.Current.CancellationToken);

        // A synchronous test observer makes order and content deterministic without depending
        // on the xUnit synchronization context draining Progress<T> callbacks.
        Assert.Equal(2, steps.Count);
        // Missing and present are different waits and say so.
        Assert.Contains("model-a", steps[0].Message, StringComparison.Ordinal);
        Assert.Contains("downloading", steps[0].Message, StringComparison.Ordinal);
        Assert.Equal(0, steps[0].Completed);
        Assert.Contains("model-b", steps[1].Message, StringComparison.Ordinal);
        Assert.Contains("video memory", steps[1].Message, StringComparison.Ordinal);
        Assert.Equal(1, steps[1].Completed);
        Assert.All(steps, step => Assert.Equal(2, step.Total));
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
                "run localai model pull --model model-a --catalog-version signed-7 --progress-json",
                "run localai model preflight --model model-a --context 2048 --catalog-version signed-7",
                "run localai model pull --model model-b --catalog-version signed-7 --progress-json",
                "run localai model preflight --model model-b --context 4096 --catalog-version signed-7",
            ],
            runner.Calls.Select(call => string.Join(' ', call.Arguments)));
    }

    [Fact]
    public async Task Same_model_at_multiple_signed_contexts_pulls_once_and_preflights_each_context()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            Success(Pull("model-a", "signed-7")),
            Success(Preflight("model-a", 2048, 80, 80, true)),
            Success(Preflight("model-a", 8192, 80, 80, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [
                Request("a", "model-a", 2048, "signed-7"),
                Request("b", "model-a", 8192, "signed-7"),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Models.Count);
        Assert.True(result.Models[0].PullCompleted);
        Assert.False(result.Models[1].PullAttempted);
        Assert.Single(runner.Calls, call =>
            call.Arguments.Contains("pull", StringComparer.Ordinal));
        Assert.Equal(2, runner.Calls.Count(call =>
            call.Arguments.Contains("preflight", StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Selection_absent_from_signed_manifest_stops_before_status()
    {
        var runner = new RecordingProcessRunner();
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-z", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.LauncherTrustFailure, result.StopReason);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Pending_model_is_still_pulled_from_current_signed_catalog_then_preflighted()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], ["model-a"], "signed-7")),
            Success(Pull("model-a", "signed-7")),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelInstallOutcome.Accepted, Assert.Single(result.Models).Outcome);
        Assert.Contains(
            runner.Calls,
            call => call.Arguments.Contains("pull", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Installed_and_pending_overlap_is_valid_and_installed_model_skips_pull(
        bool requestedModelOverlaps)
    {
        var installed = requestedModelOverlaps
            ? new[] { "model-a" }
            : new[] { "model-a", "model-b" };
        var pending = requestedModelOverlaps
            ? new[] { "model-a" }
            : new[] { "model-b" };
        var runner = new RecordingProcessRunner(
            Success(Status(installed, pending, "signed-7")),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.None, result.StopReason);
        Assert.Equal(BrokerModelInstallOutcome.Accepted, Assert.Single(result.Models).Outcome);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Arguments.Contains("pull", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("foreign-model", 2048, 100)]
    [InlineData("MODEL-A", 2048, 100)]
    public async Task Foreign_stale_or_case_changed_fallback_choice_stops_before_status(
        string fallbackModel,
        int fallbackContext,
        ulong fallbackBaseEstimate)
    {
        var runner = new RecordingProcessRunner();
        var choices = new[]
        {
            Choice("model-a", 2048, 100),
            Choice(fallbackModel, fallbackContext, fallbackBaseEstimate),
        };
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7", choices: choices)],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.LauncherTrustFailure, result.StopReason);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(99, 100)]
    [InlineData(100, 99)]
    public async Task Selected_choice_with_different_release_estimates_stops_before_status(
        ulong baseEstimate,
        ulong downloadSize)
    {
        var runner = new RecordingProcessRunner();
        var installer = Installer(runner, Launcher());
        var choices = new[]
        {
            Choice(
                "model-a",
                2048,
                baseEstimate,
                downloadSize: downloadSize),
        };

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7", choices: choices)],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.LauncherTrustFailure, result.StopReason);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("required")]
    [InlineData("adapter")]
    public async Task Internally_inconsistent_recommendation_stops_before_status(
        string corruption)
    {
        var runner = new RecordingProcessRunner();
        var selected = Choice("model-a", 2048, 100);
        var fallback = Choice("model-b", 2048, 60);
        var corrupted = corruption switch
        {
            "runtime" => CopyChoice(
                fallback,
                runtimeReserveBytes: fallback.RuntimeReserveBytes - 1),
            "required" => CopyChoice(
                fallback,
                requiredBytes: fallback.RequiredBytes + 1),
            "adapter" => CopyChoice(
                fallback,
                availableDedicatedBytes: fallback.AvailableDedicatedBytes - 1,
                headroomBytes: fallback.HeadroomBytes - 1),
            _ => throw new InvalidOperationException(),
        };
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request(
                "a",
                "model-a",
                2048,
                "signed-7",
                choices: [selected, corrupted])],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.LauncherTrustFailure, result.StopReason);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(false, true, "a", "model-a", "signed-7")]
    [InlineData(true, false, "a", "model-a", "signed-7")]
    [InlineData(true, true, "", "model-a", "signed-7")]
    [InlineData(true, true, "a", "../unsafe", "signed-7")]
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
            Choice("model-a", 2048, 100),
            Choice("model-b", 2048, 60),
            Choice("model-a", 8192, 100),
            Choice("model-d", 2048, 110),
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
            [("model-a", 2048), ("model-b", 2048), ("model-d", 2048)],
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
                    1, "preflight", false, "model-a", 2048, "signed-7", "residency_rejected")),
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
                "signed-7",
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
            Trust(),
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
            runner, Launcher(), layout, Trust(), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerModelInstaller(
            runner, Launcher(), layout, Trust(), TimeSpan.FromMinutes(31)));
        Assert.Empty(runner.Calls);
    }

    /// <summary>
    /// A pull is a download, and its duration belongs to the network rather than to this
    /// product. gpt-oss:20b is about 12.8 GB, so while every command shared the 30-minute
    /// ceiling it could not be finished below roughly 8 MB/s, and no setting helped because a
    /// larger timeout threw.
    /// </summary>
    [Fact]
    public async Task A_pull_is_given_far_longer_than_a_command()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            Success(Pull("model-a", "signed-7")),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var installer = Installer(runner, Launcher());

        await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        var timeouts = runner.Calls
            .ToDictionary(call => call.Arguments[3], call => call.Timeout, StringComparer.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(5), timeouts["status"]);
        Assert.Equal(TimeSpan.FromMinutes(5), timeouts["preflight"]);
        Assert.True(
            timeouts["pull"] > TimeSpan.FromHours(1),
            $"The pull was given {timeouts["pull"]}, which is a command's deadline, not a download's.");
    }

    [Fact]
    public async Task A_caller_that_wants_to_give_up_sooner_can_say_so()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            Success(Pull("model-a", "signed-7")),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var installer = new BrokerModelInstaller(
            runner,
            Launcher(),
            layout,
            Trust(),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(2));

        await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            TimeSpan.FromHours(2),
            runner.Calls.Single(call => call.Arguments[3] == "pull").Timeout);
    }

    [Fact]
    public void The_pull_timeout_is_positive_and_bounded()
    {
        var runner = new RecordingProcessRunner();

        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerModelInstaller(
            runner, Launcher(), layout, Trust(), TimeSpan.FromMinutes(5), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerModelInstaller(
            runner, Launcher(), layout, Trust(), TimeSpan.FromMinutes(5), TimeSpan.FromHours(13)));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Active_version_batch_lease_requires_exact_signed_version()
    {
        var root = CreateActivationRoot("v1");

        Assert.Throws<CurrentPointerChangedException>(() =>
            ActiveVersionBatchLease.Acquire(
                root,
                "v2",
                TimeSpan.Zero,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Active_version_batch_lease_blocks_activation_allows_child_shared_and_detects_drift()
    {
        var root = CreateActivationRoot("v1");
        using var batch = ActiveVersionBatchLease.Acquire(
            root,
            "v1",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        using (ActivationCoordinator.AcquireShared(
                   root,
                   TimeSpan.FromSeconds(1),
                   TestContext.Current.CancellationToken))
        {
            var failure = Assert.Throws<ActivationCoordinationException>(() =>
                ActivationCoordinator.AcquireExclusive(
                    root,
                    TimeSpan.Zero,
                    TestContext.Current.CancellationToken));
            Assert.Equal("version_in_use", failure.Code);
        }

        File.WriteAllBytes(
            Path.Combine(root, "current.json"),
            CurrentPointerSnapshot.CreateCanonicalBytes("v2"));
        Assert.Throws<CurrentPointerChangedException>(batch.Revalidate);
    }

    [Fact]
    public async Task Cancellation_while_activation_gate_is_blocked_stops_before_process_calls()
    {
        var root = CreateActivationRoot("v1");
        var blocker = HoldStartupGateAsync(
            root,
            TestContext.Current.CancellationToken);
        await blocker.Held;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(75));
        var runner = new RecordingProcessRunner();
        var trust = Trust();
        // The budget is a minute so that "noticed the cancellation" and "sat here until the
        // budget ran out" are a long way apart. It used to be five seconds against an assertion
        // that the whole thing finished within one, which measured how busy the machine was
        // rather than which of the two happened: a loaded runner took 1.7 seconds to do the right
        // thing and failed.
        trust.Acquire = token => ActiveVersionBatchLease.Acquire(
            root,
            "v1",
            GateBudget,
            token);
        var installer = Installer(runner, Launcher(), trust);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        BrokerModelInstallBatchResult result;
        try
        {
            result = await installer.InstallAsync(
                [Request("a", "model-a", 2048, "signed-7")],
                cancellation.Token);
        }
        finally
        {
            blocker.Release();
            await blocker.Completion;
        }

        stopwatch.Stop();
        Assert.Equal(BrokerModelBatchStopReason.Cancelled, result.StopReason);
        Assert.Equal("cancelled", result.Code);
        Assert.Empty(runner.Calls);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, GateBudget / 3);
        using var reacquired = ActivationCoordinator.AcquireStartupGate(
            root,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Activation_gate_timeout_is_distinct_and_stops_before_process_calls()
    {
        var root = CreateActivationRoot("v1");
        var blocker = HoldStartupGateAsync(
            root,
            TestContext.Current.CancellationToken);
        await blocker.Held;
        var runner = new RecordingProcessRunner();
        var trust = Trust();
        trust.Acquire = token => ActiveVersionBatchLease.Acquire(
            root,
            "v1",
            TimeSpan.FromMilliseconds(75),
            token);
        var installer = Installer(runner, Launcher(), trust);

        BrokerModelInstallBatchResult result;
        try
        {
            result = await installer.InstallAsync(
                [Request("a", "model-a", 2048, "signed-7")],
                TestContext.Current.CancellationToken);
        }
        finally
        {
            blocker.Release();
            await blocker.Completion;
        }

        Assert.Equal(BrokerModelBatchStopReason.TimedOut, result.StopReason);
        Assert.Equal("active_version_lease_timeout", result.Code);
        Assert.Empty(runner.Calls);
        using var reacquired = ActivationCoordinator.AcquireStartupGate(
            root,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_while_current_lock_is_blocked_releases_gate_and_stops_processes()
    {
        var root = CreateActivationRoot("v1");
        var lockPath = Path.Combine(root, "current.lock");
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(75));
        var runner = new RecordingProcessRunner();
        var trust = Trust();
        trust.Acquire = token => ActiveVersionBatchLease.Acquire(
            root,
            "v1",
            TimeSpan.FromSeconds(5),
            token);
        var installer = Installer(runner, Launcher(), trust);
        BrokerModelInstallBatchResult result;

        using (new FileStream(
                   lockPath,
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            result = await installer.InstallAsync(
                [Request("a", "model-a", 2048, "signed-7")],
                cancellation.Token);
        }

        Assert.Equal(BrokerModelBatchStopReason.Cancelled, result.StopReason);
        Assert.Equal("cancelled", result.Code);
        Assert.Empty(runner.Calls);
        using var acquired = ActiveVersionBatchLease.Acquire(
            root,
            "v1",
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
    }

    private string CreateActivationRoot(string version)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "localai-model-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        activationRoots.Add(root);
        File.WriteAllBytes(
            Path.Combine(root, "current.json"),
            CurrentPointerSnapshot.CreateCanonicalBytes(version));
        return root;
    }

    private static GateBlocker HoldStartupGateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var held = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(() =>
        {
            using var gate = ActivationCoordinator.AcquireStartupGate(
                root,
                TimeSpan.FromSeconds(1),
                cancellationToken);
            held.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        return new GateBlocker(held.Task, completion, release.SetResult);
    }

    public void Dispose()
    {
        foreach (var root in activationRoots)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private BrokerModelInstaller Installer(
        RecordingProcessRunner runner,
        RecordingLauncher launcher,
        RecordingTrust? trust = null) =>
        new(runner, launcher, layout, trust ?? Trust(), TimeSpan.FromMinutes(5));

    private RecordingLauncher Launcher() => new(layout.LauncherPath);

    private static RecordingTrust Trust() => new(
        "signed-7",
        [
            new ManifestModel("model-a", 2048, 100, 100),
            new ManifestModel("model-a", 8192, 100, 100),
            new ManifestModel("model-b", 2048, 100, 60),
            new ManifestModel("model-b", 4096, 100, 60),
            new ManifestModel("model-c", 2048, 100, 120),
            new ManifestModel("model-d", 2048, 100, 110),
            new ManifestModel("qwen3.5:9b", 8192, 100, 100),
        ]);

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
            choices ?? [Choice(
                model,
                context,
                SignedBaseEstimate(model))]);

    private static ulong SignedBaseEstimate(string model) => model switch
    {
        "model-b" => 60,
        "model-c" => 120,
        "model-d" => 110,
        _ => 100,
    };

    private static ModelRecommendationChoice Choice(
        string model,
        int context,
        ulong signedBaseEstimate,
        ulong downloadSize = 100)
    {
        var policy = ModelMemoryReservePolicy.ConservativeProduction;
        var contextReserve = checked((ulong)context * policy.ContextBytesPerToken);
        var required = checked(
            signedBaseEstimate +
            policy.FixedRuntimeReserveBytes +
            contextReserve);
        const ulong available = 4UL * 1024 * 1024 * 1024;
        var enabled = required <= available;
        return new(
            model,
            context,
            downloadSize,
            signedBaseEstimate,
            policy.FixedRuntimeReserveBytes,
            contextReserve,
            required,
            available,
            available >= required ? available - required : 0,
            available >= required ? 0 : required - available,
            enabled,
            "test choice");
    }

    private static ModelRecommendationChoice CopyChoice(
        ModelRecommendationChoice choice,
        ulong? runtimeReserveBytes = null,
        ulong? requiredBytes = null,
        ulong? availableDedicatedBytes = null,
        ulong? headroomBytes = null) =>
        new(
            choice.Name,
            choice.ContextTokens,
            choice.SignedDownloadSizeBytes,
            choice.SignedBaseEstimateBytes,
            runtimeReserveBytes ?? choice.RuntimeReserveBytes,
            choice.ContextReserveBytes,
            requiredBytes ?? choice.RequiredBytes,
            availableDedicatedBytes ?? choice.AvailableDedicatedBytes,
            headroomBytes ?? choice.HeadroomBytes,
            choice.OverBudgetBytes,
            choice.IsEnabled,
            choice.Explanation);

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
            "signed-7",
            size,
            sizeVram,
            fullyResident,
            VerifiedUtc));

    private static string Json<T>(T value) =>
        JsonSerializer.Serialize(value, LocalAiJson.Strict) + Environment.NewLine;

    /// <summary>
    /// A pull now reports where it has got to, and it reports on standard error, which this
    /// installer refuses from a child process. The refusal is what catches a binary that has
    /// started printing warnings, so it is narrowed rather than lifted: progress objects are
    /// expected on a run that asked for them, and nothing else ever is.
    /// </summary>
    [Fact]
    public async Task A_pull_that_reports_its_progress_is_not_a_protocol_failure()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            new ProcessResult(
                0,
                Pull("model-a", "signed-7"),
                """
                {"schemaVersion":1,"operation":"pull","model":"model-a","phase":"preparing"}
                {"schemaVersion":1,"operation":"pull","model":"model-a","phase":"downloading","completedBytes":1,"totalBytes":2}
                """,
                false,
                false),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(result.Models).PullCompleted);
    }

    /// <summary>
    /// A twelve-hour download reports thousands of lines and the runner keeps a bounded prefix,
    /// so a truncated capture is the ordinary case rather than a fault. The answer is on standard
    /// output, and a truncated answer still fails as it always did.
    /// </summary>
    [Fact]
    public async Task Progress_that_did_not_all_fit_is_still_not_a_failure()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            new ProcessResult(
                0,
                Pull("model-a", "signed-7"),
                """
                {"schemaVersion":1,"operation":"pull","model":"model-a","phase":"downloading","completedBytes":1,"totalBytes":2}
                {"schemaVersion":1,"operation":"pull","mod
                """,
                false,
                false,
                StandardErrorTruncated: true),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(result.Models).PullCompleted);
    }

    /// <summary>
    /// The half that must not be lost: anything on standard error that is not what was asked for
    /// still fails the install, because that is the only thing standing between a child process
    /// that has started printing warnings and an installation that ignores them.
    /// </summary>
    [Fact]
    public async Task A_pull_that_writes_something_else_still_fails()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            new ProcessResult(
                0,
                Pull("model-a", "signed-7"),
                "warning: the launcher is unsigned",
                false,
                false),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var installer = Installer(runner, Launcher());

        var result = await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerModelBatchStopReason.ProtocolFailure, result.StopReason);
    }

    /// <summary>
    /// The rail's whole job is to move. A model download is minutes to hours, and until now the
    /// window showed one motionless line for all of it — the figures existed in the child process
    /// and stopped at its standard error.
    /// </summary>
    [Fact]
    public async Task The_rail_is_given_the_figures_while_the_download_runs()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            new ProcessResult(
                0,
                Pull("model-a", "signed-7"),
                """
                {"schemaVersion":1,"operation":"pull","model":"model-a","phase":"downloading","completedBytes":5046586573,"totalBytes":13743895347}
                {"schemaVersion":1,"operation":"pull","model":"model-a","phase":"verifying"}
                """,
                false,
                false),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var reported = new List<ModelProvisioningProgress>();
        var installer = Installer(runner, Launcher());

        await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            new ImmediateProgress<ModelProvisioningProgress>(reported.Add),
            TestContext.Current.CancellationToken);

        Assert.Contains(reported, step => step.Message.Contains("4.7", StringComparison.Ordinal) &&
                                          step.Message.Contains("12.8", StringComparison.Ordinal));
        Assert.Contains(reported, step => step.Message.Contains("model-a", StringComparison.Ordinal));
    }

    /// <summary>
    /// The run log does not scroll, so a hundred byte counts per model would push everything worth
    /// reading out of sight. The rail takes every report; the log keeps the milestones.
    /// </summary>
    [Fact]
    public async Task The_byte_counts_are_not_milestones_and_the_phases_are()
    {
        var runner = new RecordingProcessRunner(
            Success(Status([], [], "signed-7")),
            new ProcessResult(
                0,
                Pull("model-a", "signed-7"),
                """
                {"schemaVersion":1,"operation":"pull","model":"model-a","phase":"downloading","completedBytes":1,"totalBytes":2}
                {"schemaVersion":1,"operation":"pull","model":"model-a","phase":"downloading","completedBytes":2,"totalBytes":2}
                """,
                false,
                false),
            Success(Preflight("model-a", 2048, 80, 80, true)));
        var reported = new List<ModelProvisioningProgress>();
        var installer = Installer(runner, Launcher());

        await installer.InstallAsync(
            [Request("a", "model-a", 2048, "signed-7")],
            new ImmediateProgress<ModelProvisioningProgress>(reported.Add),
            TestContext.Current.CancellationToken);

        var figures = reported
            .Where(step => step.Message.Contains(" of ", StringComparison.Ordinal) ||
                           step.Message.Contains(" из ", StringComparison.Ordinal))
            .ToArray();

        // Asserted before the rest: without it the check below passes over an empty set, and
        // a test that is green because nothing happened is worse than no test.
        Assert.Equal(2, figures.Length);
        Assert.All(figures, step => Assert.False(step.IsMilestone));
        Assert.Contains(reported, step => step.IsMilestone);
    }

    private static ProcessResult Success(string stdout) =>
        new(0, stdout, string.Empty, false, false);

    private sealed class RecordingLauncher(string canonicalPath) : ITrustedStableLauncher
    {
        public string CanonicalPath { get; } = canonicalPath;
        public int RevalidationCount { get; private set; }
        public void Revalidate() => RevalidationCount++;
    }

    private sealed class RecordingTrust(
        string catalogVersion,
        IReadOnlyList<ManifestModel> models) : IModelInstallTrust
    {
        public string CatalogVersion { get; } = catalogVersion;
        public IReadOnlyList<ManifestModel> Models { get; } = models;
        public Func<CancellationToken, IActiveVersionBatchLease>? Acquire { get; set; }
        public void RevalidatePackage() { }
        public IActiveVersionBatchLease AcquireActiveVersion(
            CancellationToken cancellationToken) =>
            Acquire?.Invoke(cancellationToken) ?? new RecordingActiveVersionLease();
    }

    private sealed class RecordingActiveVersionLease : IActiveVersionBatchLease
    {
        public void Revalidate() { }
        public void Dispose() { }
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

        /// <summary>
        /// Hands over the standard error this result carries, line by line, before returning it.
        /// The real runner does this while the child is still running; what matters to the
        /// installer is the same either way — that it sees the lines at all.
        /// </summary>
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            Action<string> onStandardErrorLine,
            CancellationToken cancellationToken)
        {
            Calls.Add(new Call(executable, arguments.ToArray(), timeout));
            var result = results.Dequeue();
            foreach (var line in result.StandardError.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    onStandardErrorLine(trimmed);
                }
            }

            return Task.FromResult(result);
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record Call(
        string Executable,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout);

    private sealed record GateBlocker(
        Task Held,
        Task Completion,
        Action Release);

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
