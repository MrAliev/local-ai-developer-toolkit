using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Dependencies;

public interface IDependencyRedetector
{
    Task<DependencySnapshot> DetectAsync(
        DependencyDefinition dependency,
        CancellationToken cancellationToken);
}

public enum DependencyInstallOutcome
{
    InvalidInput,
    UnsupportedAction,
    UnsupportedPackage,
    UnsupportedVersion,
    NotSelected,
    ConsentDeclined,
    OfficialInstallerOffered,
    WingetUnavailable,
    WingetExecutableUntrusted,
    WingetExecutableChanged,
    CallerCancelled,
    ProcessCancelled,
    ElevationRequired,
    ElevationDenied,
    TimedOut,
    ExternalStateIndeterminate,
    InstallFailed,
    RedetectionMissing,
    VerifiedSuccess,
}

public sealed record OfficialInstallerOffer(
    string DisplayName,
    string PackageId,
    Uri Uri);

public sealed record DependencyInstallResult(
    DependencyInstallOutcome Outcome,
    DependencyAction? Action,
    DependencyDefinition? Dependency,
    DependencySnapshot? Before,
    DependencySnapshot? After,
    OfficialInstallerOffer? OfficialInstallerOffer,
    NonTransactionalEffect? NonTransactionalEffect,
    int? ExitCode,
    string? Reason,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated);

public sealed class WingetDependencyInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RedetectionTimeout =
        TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _processRunner;
    private readonly IDependencyRedetector _redetector;
    private readonly IWinGetExecutableTrust _executableTrust;

    public WingetDependencyInstaller(
        IProcessRunner processRunner,
        IDependencyRedetector redetector,
        IWinGetExecutableTrust executableTrust)
    {
        _processRunner =
            processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _redetector =
            redetector ?? throw new ArgumentNullException(nameof(redetector));
        _executableTrust =
            executableTrust ?? throw new ArgumentNullException(nameof(executableTrust));
    }

    public async Task<DependencyInstallResult> InstallAsync(
        DependencyAction action,
        DependencySnapshot winGet,
        DependencySnapshot before,
        CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return Result(
                DependencyInstallOutcome.InvalidInput,
                action,
                reason: "A dependency action is required.");
        }

        if (!DependencyCatalog.TryGetByActionId(
                action.ActionId,
                out var dependency))
        {
            return Result(
                DependencyInstallOutcome.UnsupportedAction,
                action,
                reason: "The dependency action is not supported.");
        }

        if (!DependencyCatalog.TryGetByPackageId(
                action.PackageId,
                out var package) ||
            !ReferenceEquals(package, dependency))
        {
            return Result(
                DependencyInstallOutcome.UnsupportedPackage,
                action,
                dependency,
                reason: "The dependency package ID is not supported for this action.");
        }

        if (!IsExactVersion(action.Version))
        {
            return Result(
                DependencyInstallOutcome.UnsupportedVersion,
                action,
                dependency,
                reason: "The dependency version must be an exact immutable version token.");
        }

        if (!action.Selected)
        {
            return Result(
                DependencyInstallOutcome.NotSelected,
                action,
                dependency,
                reason: "The dependency action was not selected.");
        }

        if (!action.ConsentGranted)
        {
            return Result(
                DependencyInstallOutcome.ConsentDeclined,
                action,
                dependency,
                reason: "Explicit installation consent was not granted.");
        }

        if (!IsDependencySnapshot(before, dependency.DisplayName) ||
            winGet is null ||
            !string.Equals(winGet.Name, "WinGet", StringComparison.Ordinal) ||
            !Enum.IsDefined(winGet.State))
        {
            return Result(
                DependencyInstallOutcome.InvalidInput,
                action,
                dependency,
                before,
                reason: "Dependency snapshots are invalid or do not match the action.");
        }

        if (winGet.State == DependencyState.NotFound)
        {
            return Result(
                DependencyInstallOutcome.OfficialInstallerOffered,
                action,
                dependency,
                before,
                offer: CreateOffer(dependency),
                reason: "WinGet was not found; use the official installer offer.");
        }

        if (winGet.State == DependencyState.Failed)
        {
            return Result(
                DependencyInstallOutcome.WingetUnavailable,
                action,
                dependency,
                before,
                offer: CreateOffer(dependency),
                reason: "WinGet detection failed.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(
                DependencyInstallOutcome.CallerCancelled,
                action,
                dependency,
                before,
                reason: "The caller cancelled before process execution.");
        }

        if (string.IsNullOrWhiteSpace(winGet.ExecutablePath))
        {
            return Result(
                DependencyInstallOutcome.InvalidInput,
                action,
                dependency,
                before,
                reason: "The WinGet snapshot has no executable path.");
        }

        var resolved = _executableTrust.Resolve(winGet.ExecutablePath);
        if (resolved.Status != ExecutableTrustStatus.Trusted ||
            resolved.Executable is null)
        {
            return Result(
                DependencyInstallOutcome.WingetExecutableUntrusted,
                action,
                dependency,
                before,
                reason: "WinGet executable trust validation failed.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(
                DependencyInstallOutcome.CallerCancelled,
                action,
                dependency,
                before,
                reason: "The caller cancelled before process execution.");
        }

        var revalidated = _executableTrust.Revalidate(resolved.Executable);
        if (revalidated.Status == ExecutableTrustStatus.Changed)
        {
            return Result(
                DependencyInstallOutcome.WingetExecutableChanged,
                action,
                dependency,
                before,
                reason: "WinGet changed after initial trust validation.");
        }

        if (revalidated.Status != ExecutableTrustStatus.Trusted ||
            revalidated.Executable is null)
        {
            return Result(
                DependencyInstallOutcome.WingetExecutableUntrusted,
                action,
                dependency,
                before,
                reason: "Final WinGet trust validation failed.");
        }

        ProcessResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                    revalidated.Executable.CanonicalPath,
                    BuildArguments(dependency, action.Version),
                    InstallTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessTerminationException)
        {
            return Result(
                DependencyInstallOutcome.ExternalStateIndeterminate,
                action,
                dependency,
                before,
                reason: "WinGet process termination could not be verified.");
        }
        catch (OperationCanceledException)
        {
            return Result(
                DependencyInstallOutcome.ExternalStateIndeterminate,
                action,
                dependency,
                before,
                reason: "WinGet execution was cancelled after launch.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result(
                DependencyInstallOutcome.InstallFailed,
                action,
                dependency,
                before,
                reason: "Trusted WinGet execution failed.");
        }

        if (processResult.TimedOut)
        {
            return Result(
                DependencyInstallOutcome.TimedOut,
                action,
                dependency,
                before,
                processResult: processResult,
                reason: "The WinGet installation timed out.");
        }

        if (processResult.Cancelled)
        {
            return Result(
                DependencyInstallOutcome.ExternalStateIndeterminate,
                action,
                dependency,
                before,
                processResult: processResult,
                reason: "WinGet execution ended with indeterminate external state.");
        }

        var failure = ClassifyExitCode(processResult.ExitCode);
        if (failure is not null)
        {
            return Result(
                failure.Value,
                action,
                dependency,
                before,
                exitCode: processResult.ExitCode,
                processResult: processResult,
                reason: FailureReason(failure.Value));
        }

        var redetection = await RedetectAfterSuccessAsync(dependency)
            .ConfigureAwait(false);
        if (!redetection.Completed)
        {
            return Result(
                DependencyInstallOutcome.ExternalStateIndeterminate,
                action,
                dependency,
                before,
                exitCode: processResult.ExitCode,
                processResult: processResult,
                reason: "Post-install dependency state could not be determined.");
        }

        var after = redetection.Snapshot;
        if (!IsRecognized(after, dependency, action.Version))
        {
            return Result(
                DependencyInstallOutcome.RedetectionMissing,
                action,
                dependency,
                before,
                after,
                exitCode: processResult.ExitCode,
                processResult: processResult,
                reason: "The dependency was not recognized after WinGet completed.");
        }

        var effect = new NonTransactionalEffect(
            $"effect.{action.ActionId}.external",
            action.ActionId,
            $"{dependency.DisplayName} {action.Version} was installed externally; " +
            "this installer does not automatically uninstall or roll back that package.");
        return Result(
            DependencyInstallOutcome.VerifiedSuccess,
            action,
            dependency,
            before,
            after,
            effect: effect,
            exitCode: processResult.ExitCode,
            processResult: processResult);
    }

    private async Task<RedetectionAttempt> RedetectAfterSuccessAsync(
        DependencyDefinition dependency)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = new CancellationTokenSource(
                RedetectionTimeout);
            try
            {
                var detection = _redetector.DetectAsync(
                    dependency,
                    timeout.Token);
                var snapshot = await detection
                    .WaitAsync(RedetectionTimeout)
                    .ConfigureAwait(false);
                return new RedetectionAttempt(true, snapshot);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or TimeoutException or
                IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                if (attempt == 1 || exception is TimeoutException)
                {
                    return new RedetectionAttempt(false, null);
                }
            }
        }

        return new RedetectionAttempt(false, null);
    }

    private static IReadOnlyList<string> BuildArguments(
        DependencyDefinition dependency,
        string version) =>
        [
            "install",
            "--id",
            dependency.PackageId,
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
        ];

    private static bool IsDependencySnapshot(
        DependencySnapshot? snapshot,
        string expectedName) =>
        snapshot is not null &&
        Enum.IsDefined(snapshot.State) &&
        string.Equals(snapshot.Name, expectedName, StringComparison.Ordinal);

    private static bool IsRecognized(
        DependencySnapshot? snapshot,
        DependencyDefinition dependency,
        string requestedVersion) =>
        IsDependencySnapshot(snapshot, dependency.DisplayName) &&
        snapshot!.State == DependencyState.Detected &&
        !string.IsNullOrWhiteSpace(snapshot.ExecutablePath) &&
        string.Equals(
            Path.GetFileName(snapshot.ExecutablePath),
            dependency.ExecutableName,
            StringComparison.OrdinalIgnoreCase) &&
        IsRequestedVersion(
            dependency,
            requestedVersion,
            snapshot.Version);

    private static bool IsRequestedVersion(
        DependencyDefinition dependency,
        string requestedVersion,
        string? detectedVersion)
    {
        if (string.IsNullOrWhiteSpace(detectedVersion))
        {
            return false;
        }

        if (string.Equals(
                detectedVersion,
                requestedVersion,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!ReferenceEquals(dependency, DependencyCatalog.Git))
        {
            return false;
        }

        const string gitVersionPrefix = "git version ";
        var normalized = detectedVersion.StartsWith(
            gitVersionPrefix,
            StringComparison.Ordinal)
            ? detectedVersion[gitVersionPrefix.Length..]
            : detectedVersion;
        if (string.Equals(
                normalized,
                requestedVersion,
                StringComparison.Ordinal))
        {
            return true;
        }

        var windowsBuildPrefix = $"{requestedVersion}.windows.";
        if (!normalized.StartsWith(
                windowsBuildPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        var build = normalized[windowsBuildPrefix.Length..];
        return build.Length > 0 &&
               build.All(character => char.IsAsciiDigit(character));
    }

    private static bool IsExactVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) ||
            version.Length > 128 ||
            !char.IsAsciiDigit(version[0]) ||
            !char.IsAsciiLetterOrDigit(version[^1]) ||
            version.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return version.All(
            character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_');
    }

    private static DependencyInstallOutcome? ClassifyExitCode(int? exitCode)
    {
        if (exitCode == 0)
        {
            return null;
        }

        return exitCode switch
        {
            1223 or unchecked((int)0x800704C7) or
            1602 or unchecked((int)0x80070642) or
            unchecked((int)0x8A150005) or
            unchecked((int)0x8A15010C) =>
                DependencyInstallOutcome.ProcessCancelled,
            740 or unchecked((int)0x800702E4) or
            unchecked((int)0x8A150019) =>
                DependencyInstallOutcome.ElevationRequired,
            _ => DependencyInstallOutcome.InstallFailed,
        };
    }

    private static string FailureReason(
        DependencyInstallOutcome outcome) =>
        outcome switch
        {
            DependencyInstallOutcome.ProcessCancelled =>
                "WinGet reported that installation was cancelled.",
            DependencyInstallOutcome.ElevationRequired =>
                "WinGet reported that administrator privileges are required.",
            DependencyInstallOutcome.ElevationDenied =>
                "WinGet reported that elevation was denied.",
            _ => "WinGet installation failed.",
        };

    private static OfficialInstallerOffer CreateOffer(
        DependencyDefinition dependency) =>
        new(
            dependency.DisplayName,
            dependency.PackageId,
            dependency.OfficialInstallerUri);

    private static DependencyInstallResult Result(
        DependencyInstallOutcome outcome,
        DependencyAction? action,
        DependencyDefinition? dependency = null,
        DependencySnapshot? before = null,
        DependencySnapshot? after = null,
        OfficialInstallerOffer? offer = null,
        NonTransactionalEffect? effect = null,
        int? exitCode = null,
        ProcessResult? processResult = null,
        string? reason = null) =>
        new(
            outcome,
            action,
            dependency,
            before,
            after,
            offer,
            effect,
            exitCode,
            reason,
            processResult?.StandardOutputTruncated ?? false,
            processResult?.StandardErrorTruncated ?? false);

    private sealed record RedetectionAttempt(
        bool Completed,
        DependencySnapshot? Snapshot);
}
