using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

public sealed class WingetDependencyInstallerTests
{
    private static readonly TimeSpan ExpectedTimeout = TimeSpan.FromMinutes(15);

    [Fact]
    public void Catalog_is_an_immutable_exact_allowlist_with_official_https_fallbacks()
    {
        Assert.Equal(
            ["Git.Git", "Ollama.Ollama"],
            DependencyCatalog.Supported.Select(item => item.PackageId));
        Assert.All(
            DependencyCatalog.Supported,
            item =>
            {
                Assert.Equal(
                    DependencyVersionPolicy.ExactRequestedVersion,
                    item.VersionPolicy);
                Assert.Equal(Uri.UriSchemeHttps, item.OfficialInstallerUri.Scheme);
            });
        Assert.Equal(
            "https://git-scm.com/install/windows",
            DependencyCatalog.Git.OfficialInstallerUri.AbsoluteUri);
        Assert.Equal(
            "https://ollama.com/download/windows",
            DependencyCatalog.Ollama.OfficialInstallerUri.AbsoluteUri);
        Assert.False(
            DependencyCatalog.TryGetByPackageId(
                "git.git",
                out _));
        Assert.False(
            DependencyCatalog.TryGetByPackageId(
                "Other.Package",
                out _));
        Assert.Throws<NotSupportedException>(
            () => ((IList<DependencyDefinition>)DependencyCatalog.Supported)
                .Add(DependencyCatalog.Git));
    }

    [Theory]
    [InlineData(
        "dependency.git",
        "Git.Git",
        "2.50.1",
        "Git",
        @"C:\Program Files\Git\cmd\git.exe")]
    [InlineData(
        "dependency.ollama",
        "Ollama.Ollama",
        "0.11.4",
        "Ollama",
        @"C:\Users\test\AppData\Local\Programs\Ollama\ollama.exe")]
    public async Task Runs_only_the_exact_versioned_winget_argument_array(
        string actionId,
        string packageId,
        string version,
        string dependencyName,
        string detectedPath)
    {
        var runner = new RecordingProcessRunner(SucceededProcess());
        var detector = new RecordingDependencyRedetector(
            Detected(dependencyName, detectedPath, version));
        var installer = new WingetDependencyInstaller(runner, detector);

        var result = await installer.InstallAsync(
            new DependencyAction(
                actionId,
                packageId,
                version,
                Selected: true,
                ConsentGranted: true),
            Detected("WinGet", @"C:\Windows\System32\winget.exe", "1.10"),
            Missing(dependencyName),
            TestContext.Current.CancellationToken);

        Assert.Equal(DependencyInstallOutcome.VerifiedSuccess, result.Outcome);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(@"C:\Windows\System32\winget.exe", call.Executable);
        Assert.Equal(ExpectedTimeout, call.Timeout);
        Assert.Equal(
            [
                "install",
                "--id",
                packageId,
                "--exact",
                "--source",
                "winget",
                "--version",
                version,
                "--silent",
                "--architecture",
                "x64",
                "--accept-package-agreements",
                "--accept-source-agreements",
            ],
            call.Arguments);
        Assert.Equal(packageId, Assert.Single(detector.PackageIds));
        Assert.Equal(Missing(dependencyName), result.Before);
        Assert.Equal(DependencyState.Detected, result.After!.State);
        Assert.NotNull(result.NonTransactionalEffect);
        Assert.Equal(actionId, result.NonTransactionalEffect.RelatedActionId);
        Assert.Contains(
            "does not automatically uninstall or roll back",
            result.NonTransactionalEffect.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_run_when_action_is_not_selected()
    {
        var context = NewContext();

        var result = await context.Installer.InstallAsync(
            GitAction(selected: false, consent: false),
            Detected("WinGet", "winget.exe", "1.10"),
            Missing("Git"),
            TestContext.Current.CancellationToken);

        Assert.Equal(DependencyInstallOutcome.NotSelected, result.Outcome);
        AssertNoExternalEffect(context, result);
    }

    [Fact]
    public async Task Does_not_run_when_explicit_consent_is_declined()
    {
        var context = NewContext();

        var result = await context.Installer.InstallAsync(
            GitAction(selected: true, consent: false),
            Detected("WinGet", "winget.exe", "1.10"),
            Missing("Git"),
            TestContext.Current.CancellationToken);

        Assert.Equal(DependencyInstallOutcome.ConsentDeclined, result.Outcome);
        AssertNoExternalEffect(context, result);
    }

    [Theory]
    [InlineData("dependency.unknown", "Git.Git", "2.50.1", DependencyInstallOutcome.UnsupportedAction)]
    [InlineData("dependency.git", "git.git", "2.50.1", DependencyInstallOutcome.UnsupportedPackage)]
    [InlineData("dependency.git", "Other.Package", "2.50.1", DependencyInstallOutcome.UnsupportedPackage)]
    [InlineData("dependency.git", "Git.Git", "latest", DependencyInstallOutcome.UnsupportedVersion)]
    [InlineData("dependency.git", "Git.Git", "2.50 1", DependencyInstallOutcome.UnsupportedVersion)]
    public async Task Rejects_unsupported_action_package_or_version_before_execution(
        string actionId,
        string packageId,
        string version,
        DependencyInstallOutcome expected)
    {
        var context = NewContext();

        var result = await context.Installer.InstallAsync(
            new DependencyAction(actionId, packageId, version, true, true),
            Detected("WinGet", "winget.exe", "1.10"),
            Missing("Git"),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        AssertNoExternalEffect(context, result);
    }

    [Fact]
    public async Task Rejects_invalid_inputs_and_non_winget_executable_before_execution()
    {
        var context = NewContext();

        var nullAction = await context.Installer.InstallAsync(
            null!,
            Detected("WinGet", "winget.exe", "1.10"),
            Missing("Git"),
            TestContext.Current.CancellationToken);
        var mismatchedSnapshot = await context.Installer.InstallAsync(
            GitAction(),
            Detected("WinGet", "powershell.exe", "7.5"),
            Missing("Git"),
            TestContext.Current.CancellationToken);

        Assert.Equal(DependencyInstallOutcome.InvalidInput, nullAction.Outcome);
        Assert.Equal(DependencyInstallOutcome.InvalidInput, mismatchedSnapshot.Outcome);
        Assert.Empty(context.Runner.Calls);
        Assert.Empty(context.Detector.PackageIds);
    }

    [Theory]
    [InlineData("dependency.git", "Git.Git", "2.50.1", "Git", "https://git-scm.com/install/windows")]
    [InlineData("dependency.ollama", "Ollama.Ollama", "0.11.4", "Ollama", "https://ollama.com/download/windows")]
    public async Task Missing_winget_returns_official_installer_offer_without_a_process(
        string actionId,
        string packageId,
        string version,
        string dependencyName,
        string expectedUri)
    {
        var context = NewContext();

        var result = await context.Installer.InstallAsync(
            new DependencyAction(actionId, packageId, version, true, true),
            Missing("WinGet"),
            Missing(dependencyName),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            DependencyInstallOutcome.OfficialInstallerOffered,
            result.Outcome);
        Assert.Equal(new Uri(expectedUri), result.OfficialInstallerOffer!.Uri);
        Assert.Equal(packageId, result.OfficialInstallerOffer.PackageId);
        AssertNoExternalEffect(context, result);
    }

    [Fact]
    public async Task Pre_cancelled_caller_token_never_starts_a_process()
    {
        var context = NewContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.Installer.InstallAsync(
            GitAction(),
            Detected("WinGet", "winget.exe", "1.10"),
            Missing("Git"),
            cancellation.Token);

        Assert.Equal(DependencyInstallOutcome.CallerCancelled, result.Outcome);
        AssertNoExternalEffect(context, result);
    }

    [Fact]
    public async Task Runner_reported_caller_cancellation_is_distinct_and_does_not_redetect()
    {
        var context = NewContext(
            new ProcessResult(null, "", "", TimedOut: false, Cancelled: true));

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.CallerCancelled, result.Outcome);
        Assert.Single(context.Runner.Calls);
        Assert.Empty(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Cancellation_observed_as_the_process_completes_prevents_redetection()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new CancellingProcessRunner(cancellation);
        var detector = new RecordingDependencyRedetector(
            Detected("Git", "git.exe", "2.50.1"));
        var installer = new WingetDependencyInstaller(runner, detector);

        var result = await installer.InstallAsync(
            GitAction(),
            Detected("WinGet", "winget.exe", "1.10"),
            Missing("Git"),
            cancellation.Token);

        Assert.Equal(DependencyInstallOutcome.CallerCancelled, result.Outcome);
        Assert.Equal(1, runner.CallCount);
        Assert.Empty(detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    [Theory]
    [InlineData(1223)]
    [InlineData(unchecked((int)0x800704C7))]
    [InlineData(1602)]
    [InlineData(unchecked((int)0x80070642))]
    public async Task Process_reported_cancellation_or_uac_refusal_is_distinct(
        int exitCode)
    {
        var context = NewContext(FailedProcess(exitCode));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.ProcessCancelledOrUacRefused,
            result.Outcome);
        AssertFailedWithoutRedetection(context, result);
    }

    [Theory]
    [InlineData(740, DependencyInstallOutcome.ElevationRequired)]
    [InlineData(unchecked((int)0x800702E4), DependencyInstallOutcome.ElevationRequired)]
    [InlineData(5, DependencyInstallOutcome.ElevationDenied)]
    [InlineData(unchecked((int)0x80070005), DependencyInstallOutcome.ElevationDenied)]
    public async Task Elevation_outcomes_are_classified_only_for_known_codes(
        int exitCode,
        DependencyInstallOutcome expected)
    {
        var context = NewContext(FailedProcess(exitCode));

        var result = await InstallGitAsync(context);

        Assert.Equal(expected, result.Outcome);
        AssertFailedWithoutRedetection(context, result);
    }

    [Fact]
    public async Task Timeout_is_distinct_and_does_not_redetect()
    {
        var context = NewContext(
            new ProcessResult(null, "", "", TimedOut: true, Cancelled: false));

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.TimedOut, result.Outcome);
        AssertFailedWithoutRedetection(context, result);
    }

    [Fact]
    public async Task Unverified_process_termination_is_distinct()
    {
        var runner = new RecordingProcessRunner(
            new ProcessTerminationException(
                123,
                ProcessTerminationCause.Timeout,
                "Could not verify process termination."));
        var context = NewContext(runner: runner);

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.TerminationFailed, result.Outcome);
        AssertFailedWithoutRedetection(context, result);
    }

    [Fact]
    public async Task Generic_nonzero_exit_is_an_install_failure()
    {
        var context = NewContext(FailedProcess(42));

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.InstallFailed, result.Outcome);
        Assert.Equal(42, result.ExitCode);
        AssertFailedWithoutRedetection(context, result);
    }

    [Theory]
    [InlineData(DependencyState.NotFound)]
    [InlineData(DependencyState.Failed)]
    public async Task Successful_process_requires_dependency_redetection(
        DependencyState state)
    {
        var after = new DependencySnapshot(
            "Git",
            state,
            null,
            null,
            state == DependencyState.Failed ? "probe failed" : null);
        var context = NewContext(
            SucceededProcess(),
            new RecordingDependencyRedetector(after));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.RedetectionMissing,
            result.Outcome);
        Assert.Equal(after, result.After);
        Assert.Single(context.Runner.Calls);
        Assert.Single(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Successful_process_rejects_a_redetection_for_another_dependency()
    {
        var context = NewContext(
            SucceededProcess(),
            new RecordingDependencyRedetector(
                Detected("Ollama", "ollama.exe", "0.11.4")));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.RedetectionMissing,
            result.Outcome);
        Assert.Null(result.NonTransactionalEffect);
    }

    [Theory]
    [InlineData("2.49.0")]
    [InlineData("git version 2.49.0.windows.1")]
    public async Task Successful_process_rejects_a_stale_or_different_version(
        string detectedVersion)
    {
        var context = NewContext(
            SucceededProcess(),
            new RecordingDependencyRedetector(
                Detected("Git", "git.exe", detectedVersion)));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.RedetectionMissing,
            result.Outcome);
        Assert.Equal(detectedVersion, result.After!.Version);
        Assert.Null(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Git_executable_windows_build_suffix_matches_the_requested_version()
    {
        var detectedVersion = "git version 2.50.1.windows.1";
        var context = NewContext(
            SucceededProcess(),
            new RecordingDependencyRedetector(
                Detected("Git", "git.exe", detectedVersion)));

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.VerifiedSuccess, result.Outcome);
        Assert.Equal(detectedVersion, result.After!.Version);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Cancellation_during_redetection_is_safe_and_runs_no_second_process()
    {
        using var cancellation = new CancellationTokenSource();
        var detector = new RecordingDependencyRedetector(
            _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        var context = NewContext(SucceededProcess(), detector);

        var result = await context.Installer.InstallAsync(
            GitAction(),
            Detected("WinGet", "winget.exe", "1.10"),
            Missing("Git"),
            cancellation.Token);

        Assert.Equal(DependencyInstallOutcome.CallerCancelled, result.Outcome);
        Assert.Single(context.Runner.Calls);
        Assert.Single(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    private static async Task<DependencyInstallResult> InstallGitAsync(
        TestContextData context) =>
        await context.Installer.InstallAsync(
            GitAction(),
            Detected("WinGet", "winget.exe", "1.10"),
            Missing("Git"),
            TestContext.Current.CancellationToken);

    private static TestContextData NewContext(
        ProcessResult? result = null,
        RecordingDependencyRedetector? detector = null,
        RecordingProcessRunner? runner = null)
    {
        var actualRunner =
            runner ?? new RecordingProcessRunner(result ?? SucceededProcess());
        var actualDetector =
            detector ?? new RecordingDependencyRedetector(
                Detected("Git", "git.exe", "2.50.1"));
        return new TestContextData(
            new WingetDependencyInstaller(actualRunner, actualDetector),
            actualRunner,
            actualDetector);
    }

    private static DependencyAction GitAction(
        bool selected = true,
        bool consent = true) =>
        new(
            "dependency.git",
            "Git.Git",
            "2.50.1",
            selected,
            consent);

    private static DependencySnapshot Detected(
        string name,
        string path,
        string version) =>
        new(name, DependencyState.Detected, path, version, null);

    private static DependencySnapshot Missing(string name) =>
        new(name, DependencyState.NotFound, null, null, null);

    private static ProcessResult SucceededProcess() =>
        new(0, "Installed", "", TimedOut: false, Cancelled: false);

    private static ProcessResult FailedProcess(int exitCode) =>
        new(exitCode, "", "failed", TimedOut: false, Cancelled: false);

    private static void AssertNoExternalEffect(
        TestContextData context,
        DependencyInstallResult result)
    {
        Assert.Empty(context.Runner.Calls);
        Assert.Empty(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    private static void AssertFailedWithoutRedetection(
        TestContextData context,
        DependencyInstallResult result)
    {
        Assert.Single(context.Runner.Calls);
        Assert.Empty(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    private sealed record TestContextData(
        WingetDependencyInstaller Installer,
        RecordingProcessRunner Runner,
        RecordingDependencyRedetector Detector);

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly ProcessResult? _result;
        private readonly Exception? _exception;

        public RecordingProcessRunner(ProcessResult result)
        {
            _result = result;
        }

        public RecordingProcessRunner(Exception exception)
        {
            _exception = exception;
        }

        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(
                new ProcessCall(
                    executable,
                    arguments.ToArray(),
                    timeout));
            return _exception is null
                ? Task.FromResult(_result!)
                : Task.FromException<ProcessResult>(_exception);
        }
    }

    private sealed class RecordingDependencyRedetector : IDependencyRedetector
    {
        private readonly Func<CancellationToken, DependencySnapshot> _detect;

        public RecordingDependencyRedetector(DependencySnapshot result)
            : this(_ => result)
        {
        }

        public RecordingDependencyRedetector(
            Func<CancellationToken, DependencySnapshot> detect)
        {
            _detect = detect;
        }

        public List<string> PackageIds { get; } = [];

        public Task<DependencySnapshot> DetectAsync(
            DependencyDefinition dependency,
            CancellationToken cancellationToken)
        {
            PackageIds.Add(dependency.PackageId);
            return Task.FromResult(_detect(cancellationToken));
        }
    }

    private sealed class CancellingProcessRunner(
        CancellationTokenSource cancellation) : IProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellation.Cancel();
            return Task.FromResult(SucceededProcess());
        }
    }

    private sealed record ProcessCall(
        string Executable,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout);
}
