using System.Reflection;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

public sealed class WingetDependencyInstallerTests
{
    private static readonly TimeSpan ExpectedTimeout = TimeSpan.FromMinutes(15);
    private const string SnapshotWingetPath =
        @"C:\Users\test\AppData\Local\Microsoft\WindowsApps\winget.exe";
    private const string CanonicalWingetPath =
        @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.279.0_x64__8wekyb3d8bbwe\winget.exe";

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

    [Fact]
    public void Failed_catalog_lookup_has_an_honest_nullable_out_contract()
    {
        var method = typeof(DependencyCatalog).GetMethod(
            nameof(DependencyCatalog.TryGetByPackageId))!;
        var resultParameter = method.GetParameters()[1];

        Assert.Equal(
            NullabilityState.Nullable,
            new NullabilityInfoContext()
                .Create(resultParameter)
                .WriteState);
        Assert.False(
            DependencyCatalog.TryGetByPackageId(
                "Other.Package",
                out var definition));
        Assert.Null(definition);
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
        var trust = TrustedWinget();
        var installer = new WingetDependencyInstaller(runner, detector, trust);

        var result = await installer.InstallAsync(
            new DependencyAction(
                actionId,
                packageId,
                version,
                Selected: true,
                ConsentGranted: true),
            Detected("WinGet", SnapshotWingetPath, "1.10"),
            Missing(dependencyName),
            TestContext.Current.CancellationToken);

        Assert.Equal(DependencyInstallOutcome.VerifiedSuccess, result.Outcome);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(CanonicalWingetPath, call.Executable);
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
        Assert.Equal(
            [SnapshotWingetPath],
            trust.ResolvePaths);
        Assert.Equal(
            [CanonicalWingetPath],
            trust.RevalidatedPaths);
    }

    [Fact]
    public void Production_trust_resolver_rejects_a_PATH_only_winget_name()
    {
        var trust = new WindowsWingetExecutableTrust();

        var result = trust.Resolve("winget.exe");

        Assert.Equal(ExecutableTrustStatus.InvalidPath, result.Status);
        Assert.Null(result.Executable);
    }

    [Fact]
    public async Task Untrusted_winget_signature_is_rejected_before_execution()
    {
        var trust = new RecordingWingetExecutableTrust(
            new ExecutableTrustResult(
                ExecutableTrustStatus.UntrustedPublisher,
                null));
        var context = NewContext(trust: trust);

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.WingetExecutableUntrusted,
            result.Outcome);
        AssertNoExternalEffect(context, result);
        Assert.Single(trust.ResolvePaths);
        Assert.Empty(trust.RevalidatedPaths);
    }

    [Fact]
    public async Task Changed_winget_target_is_rejected_immediately_before_launch()
    {
        var trusted = TrustedExecutable();
        var trust = new RecordingWingetExecutableTrust(
            new ExecutableTrustResult(
                ExecutableTrustStatus.Trusted,
                trusted),
            new ExecutableTrustResult(
                ExecutableTrustStatus.Changed,
                null));
        var context = NewContext(trust: trust);

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.WingetExecutableChanged,
            result.Outcome);
        AssertNoExternalEffect(context, result);
        Assert.Single(trust.ResolvePaths);
        Assert.Single(trust.RevalidatedPaths);
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
        var context = NewContext(
            trust: new RecordingWingetExecutableTrust(
                new ExecutableTrustResult(
                    ExecutableTrustStatus.InvalidPath,
                    null)));

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
        Assert.Equal(
            DependencyInstallOutcome.WingetExecutableUntrusted,
            mismatchedSnapshot.Outcome);
        Assert.Empty(context.ProcessCalls);
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
    public async Task Runner_reported_cancellation_still_journals_verified_install()
    {
        var context = NewContext(
            new ProcessResult(null, "", "", TimedOut: false, Cancelled: true));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.VerifiedInstalledWithProcessIssue,
            result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.Cancelled,
            result.ProcessDisposition);
        Assert.Equal(Missing("Git"), result.Before);
        Assert.Single(context.ProcessCalls);
        Assert.Single(context.Detector.PackageIds);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Successful_exit_is_redetected_even_when_caller_cancels_as_process_completes()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new CancellingProcessRunner(cancellation);
        var detector = new RecordingDependencyRedetector(
            Detected("Git", "git.exe", "2.50.1"));
        var installer = new WingetDependencyInstaller(
            runner,
            detector,
            TrustedWinget());

        var result = await installer.InstallAsync(
            GitAction(),
            Detected("WinGet", SnapshotWingetPath, "1.10"),
            Missing("Git"),
            cancellation.Token);

        Assert.Equal(DependencyInstallOutcome.VerifiedSuccess, result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.Succeeded,
            result.ProcessDisposition);
        Assert.Equal(1, runner.CallCount);
        Assert.Single(detector.PackageIds);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Theory]
    [InlineData(1223)]
    [InlineData(unchecked((int)0x800704C7))]
    [InlineData(1602)]
    [InlineData(unchecked((int)0x80070642))]
    [InlineData(unchecked((int)0x8A150005))]
    [InlineData(unchecked((int)0x8A15010C))]
    public async Task Official_or_wrapped_process_cancellation_is_distinct(
        int exitCode)
    {
        var context = NewContext(
            FailedProcess(exitCode),
            new RecordingDependencyRedetector(Missing("Git")));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.ProcessCancelled,
            result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.Cancelled,
            result.ProcessDisposition);
        AssertFailedAfterRedetection(context, result);
    }

    [Theory]
    [InlineData(740, DependencyInstallOutcome.ElevationRequired)]
    [InlineData(unchecked((int)0x800702E4), DependencyInstallOutcome.ElevationRequired)]
    [InlineData(unchecked((int)0x8A150019), DependencyInstallOutcome.ElevationRequired)]
    public async Task Elevation_outcomes_are_classified_only_for_known_codes(
        int exitCode,
        DependencyInstallOutcome expected)
    {
        var context = NewContext(
            FailedProcess(exitCode),
            new RecordingDependencyRedetector(Missing("Git")));

        var result = await InstallGitAsync(context);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.ElevationRequired,
            result.ProcessDisposition);
        AssertFailedAfterRedetection(context, result);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(unchecked((int)0x80070005))]
    public async Task Generic_access_denied_is_not_claimed_to_be_a_UAC_refusal(
        int exitCode)
    {
        var context = NewContext(
            FailedProcess(exitCode),
            new RecordingDependencyRedetector(Missing("Git")));

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.InstallFailed, result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.Failed,
            result.ProcessDisposition);
        AssertFailedAfterRedetection(context, result);
    }

    [Fact]
    public async Task Timeout_still_journals_verified_install()
    {
        var context = NewContext(
            new ProcessResult(null, "", "", TimedOut: true, Cancelled: false));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.VerifiedInstalledWithProcessIssue,
            result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.TimedOut,
            result.ProcessDisposition);
        Assert.Single(context.ProcessCalls);
        Assert.Single(context.Detector.PackageIds);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Theory]
    [InlineData(true, false, DependencyInstallOutcome.TimedOut, DependencyProcessDisposition.TimedOut)]
    [InlineData(false, true, DependencyInstallOutcome.ExternalStateIndeterminate, DependencyProcessDisposition.Cancelled)]
    public async Task Timeout_or_runner_cancellation_without_detection_preserves_process_classification(
        bool timedOut,
        bool cancelled,
        DependencyInstallOutcome expectedOutcome,
        DependencyProcessDisposition expectedDisposition)
    {
        var context = NewContext(
            new ProcessResult(null, "", "", timedOut, cancelled),
            new RecordingDependencyRedetector(Missing("Git")));

        var result = await InstallGitAsync(context);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedDisposition, result.ProcessDisposition);
        Assert.Single(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Unverified_process_termination_still_journals_verified_install()
    {
        var runner = new RecordingProcessRunner(
            new ProcessTerminationException(
                123,
                ProcessTerminationCause.Timeout,
                "Could not verify process termination."));
        var context = NewContext(runner: runner);

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.VerifiedInstalledWithProcessIssue,
            result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.TerminationUnverified,
            result.ProcessDisposition);
        Assert.Equal(Missing("Git"), result.Before);
        Assert.Single(context.ProcessCalls);
        Assert.Single(context.Detector.PackageIds);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Cancellation_exception_after_launch_still_journals_verified_install()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new CancellingExceptionProcessRunner(cancellation);
        var context = NewContext(runner: runner);

        var result = await context.Installer.InstallAsync(
            GitAction(),
            Detected("WinGet", SnapshotWingetPath, "1.10"),
            Missing("Git"),
            cancellation.Token);

        Assert.Equal(
            DependencyInstallOutcome.VerifiedInstalledWithProcessIssue,
            result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.Cancelled,
            result.ProcessDisposition);
        Assert.Single(context.ProcessCalls);
        Assert.Equal(Missing("Git"), result.Before);
        Assert.Single(context.Detector.PackageIds);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Generic_nonzero_exit_still_journals_verified_install()
    {
        var context = NewContext(FailedProcess(42));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.VerifiedInstalledWithProcessIssue,
            result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.Failed,
            result.ProcessDisposition);
        Assert.Equal(42, result.ExitCode);
        Assert.Single(context.ProcessCalls);
        Assert.Single(context.Detector.PackageIds);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150109), DependencyProcessDisposition.RebootRequired)]
    [InlineData(unchecked((int)0x8A15010A), DependencyProcessDisposition.RebootRequired)]
    [InlineData(unchecked((int)0x8A15010B), DependencyProcessDisposition.RebootInitiated)]
    [InlineData(unchecked((int)0x8A15010D), DependencyProcessDisposition.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A150102), DependencyProcessDisposition.ConcurrentInstallation)]
    public async Task Official_install_disposition_is_preserved_when_dependency_is_missing(
        int exitCode,
        DependencyProcessDisposition expectedDisposition)
    {
        var context = NewContext(
            FailedProcess(exitCode),
            new RecordingDependencyRedetector(Missing("Git")));

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.InstallFailed, result.Outcome);
        Assert.Equal(expectedDisposition, result.ProcessDisposition);
        Assert.Single(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Already_installed_code_with_exact_redetection_is_journaled()
    {
        var context = NewContext(
            FailedProcess(unchecked((int)0x8A15010D)));

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.VerifiedInstalledWithProcessIssue,
            result.Outcome);
        Assert.Equal(
            DependencyProcessDisposition.AlreadyInstalled,
            result.ProcessDisposition);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Process_output_is_never_exposed_in_public_failure_reason()
    {
        const string secret = "token=super-secret";
        var process = new ProcessResult(
            42,
            "sensitive stdout\r\n",
            $"{secret}\r\n\u0001",
            TimedOut: false,
            Cancelled: false,
            StandardOutputTruncated: true,
            StandardErrorTruncated: true);
        var context = NewContext(
            process,
            new RecordingDependencyRedetector(Missing("Git")));

        var result = await InstallGitAsync(context);

        Assert.Equal("WinGet installation failed.", result.Reason);
        Assert.DoesNotContain(secret, result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.Reason!,
            character => char.IsControl(character));
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task Process_start_exception_text_is_not_exposed()
    {
        const string secret = "C:\\secret\\private-token";
        var context = NewContext(
            detector: new RecordingDependencyRedetector(Missing("Git")),
            runner: new RecordingProcessRunner(
                new IOException(secret)));

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.InstallFailed, result.Outcome);
        Assert.Equal("Trusted WinGet execution failed.", result.Reason);
        Assert.DoesNotContain(secret, result.Reason, StringComparison.Ordinal);
        Assert.Single(context.ProcessCalls);
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
        Assert.Single(context.ProcessCalls);
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
    public async Task Version_matching_is_exact_ordinal()
    {
        const string requestedVersion = "2.50.1-RC1";
        var context = NewContext(
            SucceededProcess(),
            new RecordingDependencyRedetector(
                Detected("Git", "git.exe", "2.50.1-rc1")));

        var result = await context.Installer.InstallAsync(
            new DependencyAction(
                "dependency.git",
                "Git.Git",
                requestedVersion,
                true,
                true),
            Detected("WinGet", SnapshotWingetPath, "1.10"),
            Missing("Git"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            DependencyInstallOutcome.RedetectionMissing,
            result.Outcome);
        Assert.Null(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Cancelled_post_success_redetection_uses_bounded_recovery()
    {
        var call = 0;
        var detector = new RecordingDependencyRedetector(
            _ =>
            {
                call++;
                if (call == 1)
                {
                    throw new OperationCanceledException();
                }

                return Detected("Git", "git.exe", "2.50.1");
            });
        var context = NewContext(SucceededProcess(), detector);

        var result = await InstallGitAsync(context);

        Assert.Equal(DependencyInstallOutcome.VerifiedSuccess, result.Outcome);
        Assert.Single(context.ProcessCalls);
        Assert.Equal(2, context.Detector.PackageIds.Count);
        Assert.NotNull(result.NonTransactionalEffect);
    }

    [Fact]
    public async Task Failed_post_success_redetection_marks_external_state_indeterminate()
    {
        var detector = new RecordingDependencyRedetector(
            _ => throw new OperationCanceledException());
        var context = NewContext(SucceededProcess(), detector);

        var result = await InstallGitAsync(context);

        Assert.Equal(
            DependencyInstallOutcome.ExternalStateIndeterminate,
            result.Outcome);
        Assert.Single(context.ProcessCalls);
        Assert.Equal(2, context.Detector.PackageIds.Count);
        Assert.Equal(Missing("Git"), result.Before);
        Assert.Null(result.NonTransactionalEffect);
    }

    private static async Task<DependencyInstallResult> InstallGitAsync(
        TestContextData context) =>
        await context.Installer.InstallAsync(
            GitAction(),
            Detected("WinGet", SnapshotWingetPath, "1.10"),
            Missing("Git"),
            TestContext.Current.CancellationToken);

    private static TestContextData NewContext(
        ProcessResult? result = null,
        RecordingDependencyRedetector? detector = null,
        IRecordingProcessRunner? runner = null,
        RecordingWingetExecutableTrust? trust = null)
    {
        var actualRunner =
            runner ?? new RecordingProcessRunner(result ?? SucceededProcess());
        var actualDetector =
            detector ?? new RecordingDependencyRedetector(
                Detected("Git", "git.exe", "2.50.1"));
        var actualTrust = trust ?? TrustedWinget();
        return new TestContextData(
            new WingetDependencyInstaller(
                actualRunner,
                actualDetector,
                actualTrust),
            actualRunner,
            actualDetector,
            actualTrust);
    }

    private static RecordingWingetExecutableTrust TrustedWinget()
    {
        var trusted = TrustedExecutable();
        return new RecordingWingetExecutableTrust(
            new ExecutableTrustResult(
                ExecutableTrustStatus.Trusted,
                trusted),
            new ExecutableTrustResult(
                ExecutableTrustStatus.Trusted,
                trusted));
    }

    private static TrustedExecutable TrustedExecutable() =>
        new(
            CanonicalWingetPath,
            "SHA256:0123456789ABCDEF",
            "Microsoft Corporation");

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
        Assert.Empty(context.ProcessCalls);
        Assert.Empty(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    private static void AssertFailedAfterRedetection(
        TestContextData context,
        DependencyInstallResult result)
    {
        Assert.Single(context.ProcessCalls);
        Assert.Single(context.Detector.PackageIds);
        Assert.Null(result.NonTransactionalEffect);
    }

    private sealed record TestContextData(
        WingetDependencyInstaller Installer,
        IRecordingProcessRunner Runner,
        RecordingDependencyRedetector Detector,
        RecordingWingetExecutableTrust Trust)
    {
        public IReadOnlyList<ProcessCall> ProcessCalls => Runner.Calls;
    }

    private interface IRecordingProcessRunner : IProcessRunner
    {
        List<ProcessCall> Calls { get; }
    }

    private sealed class RecordingProcessRunner : IRecordingProcessRunner
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

    private sealed class RecordingWingetExecutableTrust(
        ExecutableTrustResult resolved,
        ExecutableTrustResult? revalidated = null) : IWinGetExecutableTrust
    {
        public List<string> ResolvePaths { get; } = [];
        public List<string> RevalidatedPaths { get; } = [];

        public ExecutableTrustResult Resolve(string snapshotPath)
        {
            ResolvePaths.Add(snapshotPath);
            return resolved;
        }

        public ExecutableTrustResult Revalidate(TrustedExecutable executable)
        {
            RevalidatedPaths.Add(executable.CanonicalPath);
            return revalidated ?? resolved;
        }
    }

    private sealed class CancellingProcessRunner(
        CancellationTokenSource cancellation) : IRecordingProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];
        public int CallCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Calls.Add(new ProcessCall(executable, arguments.ToArray(), timeout));
            cancellation.Cancel();
            return Task.FromResult(SucceededProcess());
        }
    }

    private sealed class CancellingExceptionProcessRunner(
        CancellationTokenSource cancellation) : IRecordingProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new ProcessCall(executable, arguments.ToArray(), timeout));
            cancellation.Cancel();
            return Task.FromException<ProcessResult>(
                new OperationCanceledException(cancellation.Token));
        }
    }

    private sealed record ProcessCall(
        string Executable,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout);
}
