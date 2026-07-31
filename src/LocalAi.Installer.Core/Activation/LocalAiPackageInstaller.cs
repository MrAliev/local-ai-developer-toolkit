using System.Security.Cryptography;
using System.Runtime.Versioning;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Activation;

public enum LocalAiPackageInstallStatus
{
    Installed,
    AlreadyInstalled,
    Refused,
    ImmutableConflict,
    RolledBack,
    RollbackFailed,
    ManualRecoveryRequired,
    Indeterminate,
    Busy,
}

public sealed record LauncherBackupMetadata(
    string Path,
    long Length,
    string Sha256);

public sealed record LocalAiPackageInstallResult(
    LocalAiPackageInstallStatus Status,
    string Version,
    string? PriorVersion,
    string VersionPath,
    LauncherBackupMetadata? LauncherBackup,
    string? Reason,
    bool InactivePublishedVersionRetained = false)
{
    public string? LauncherBackupPath => LauncherBackup?.Path;
    public string NewVersionPath => VersionPath;
    public string? InactivePublishedVersionPath =>
        InactivePublishedVersionRetained ? VersionPath : null;
}

public sealed class LocalAiPackageInstaller
{
    private static readonly HashSet<string> ReservedVersionNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);
    private readonly IProcessRunner processRunner;
    private readonly IExistingLocalAiInspector inspector;
    private readonly TimeSpan activationTimeout;
    private readonly Func<InstallationLayout, bool, InstallationLayoutLease> layoutLeaseFactory;

    public LocalAiPackageInstaller(
        IProcessRunner processRunner,
        IExistingLocalAiInspector inspector,
        TimeSpan activationTimeout) :
        this(
            processRunner,
            inspector,
            activationTimeout,
            static (layout, requireFresh) => OperatingSystem.IsWindows()
                ? InstallationLayoutLease.Acquire(layout, requireFresh)
                : throw new PlatformNotSupportedException(
                    "Protected LocalAi installation layout is available only on Windows."))
    {
    }

    internal LocalAiPackageInstaller(
        IProcessRunner processRunner,
        IExistingLocalAiInspector inspector,
        TimeSpan activationTimeout,
        Func<InstallationLayout, bool, InstallationLayoutLease> layoutLeaseFactory)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        if (activationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activationTimeout));
        }

        this.activationTimeout = activationTimeout;
        this.layoutLeaseFactory = layoutLeaseFactory
            ?? throw new ArgumentNullException(nameof(layoutLeaseFactory));
    }

    public async Task<LocalAiPackageInstallResult> InstallAsync(
        VerifiedPackage package,
        InstallationLayout layout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(package);
            ArgumentNullException.ThrowIfNull(layout);
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "LocalAi package installation is available only on Windows.");
            }

            ValidatePackageContract(package);
            InstallerTransactionLease transaction;
            try
            {
                transaction = await InstallerTransactionLease.AcquireAsync(
                        layout,
                        activationTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InstallerTransactionBusyException)
            {
                var version = package.Manifest.VersionDirectory;
                return new(
                    LocalAiPackageInstallStatus.Busy,
                    version,
                    null,
                    Path.Combine(layout.VersionsRoot, version),
                    null,
                    "Another LocalAi installation is already in progress.");
            }

            using (transaction)
            {
                return await InstallCoreAsync(
                        package,
                        layout,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (LocalAiPackageInstallationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException or
            NotSupportedException or ArgumentException or CryptographicException)
        {
            throw new LocalAiPackageInstallationException("LocalAi package installation failed.");
        }
    }

    private async Task<LocalAiPackageInstallResult> InstallCoreAsync(
        VerifiedPackage package,
        InstallationLayout layout,
        InstallerTransactionLease transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(layout);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "LocalAi package installation is available only on Windows.");
        }

        ValidatePackageContract(package);
        var existing = inspector.Inspect(layout.LocalAppData);
        var version = package.Manifest.VersionDirectory;
        var versionPath = Path.Combine(layout.VersionsRoot, version);
        if (existing.State == ExistingLocalAiState.Unrecognized)
        {
            return new(
                LocalAiPackageInstallStatus.Refused,
                version,
                existing.Version,
                versionPath,
                null,
                "The existing LocalAi installation is unrecognized.");
        }

        package.Revalidate();
        InstallationLayoutLease layoutLease;
        try
        {
            layoutLease = layoutLeaseFactory(
                layout,
                existing.State == ExistingLocalAiState.Absent);
        }
        catch (LocalAiPackageInstallationException)
        {
            return new(
                LocalAiPackageInstallStatus.Refused,
                version,
                existing.Version,
                versionPath,
                null,
                "The existing LocalAi layout is unrecognized.");
        }

        using (layoutLease)
        {
            try
            {
                try
                {
                    transaction.AttachLayout(layoutLease);
                }
                catch (IOException)
                {
                    return new(
                        LocalAiPackageInstallStatus.Busy,
                        version,
                        existing.Version,
                        versionPath,
                        null,
                        "Another LocalAi installation is already in progress.");
                }

                CurrentPointerSnapshot priorPointer;
                try
                {
                    priorPointer = ReadPointerLocked(layout);
                }
                catch (Exception exception) when (
                    exception is CurrentPointerException or ActivationCoordinationException ||
                    IsNativeHandoffFailure(exception))
                {
                    return new(
                        LocalAiPackageInstallStatus.Refused,
                        version,
                        existing.Version,
                        versionPath,
                        null,
                        "The current-version pointer is invalid or unavailable.");
                }

                if ((existing.State == ExistingLocalAiState.Compatible &&
                     (!priorPointer.Exists || !priorPointer.IsCanonical ||
                      !string.Equals(priorPointer.Version, existing.Version, StringComparison.Ordinal))) ||
                    (existing.State == ExistingLocalAiState.Absent && priorPointer.Exists))
                {
                    return new(
                        LocalAiPackageInstallStatus.Refused,
                        version,
                        existing.Version,
                        versionPath,
                        null,
                        "The current-version pointer changed during installation preparation.");
                }

                return await InstallUnderLayoutLeaseAsync(
                        package,
                        layout,
                        layoutLease,
                        version,
                        versionPath,
                        priorPointer,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                transaction.DetachLayout();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<LocalAiPackageInstallResult> InstallUnderLayoutLeaseAsync(
        VerifiedPackage package,
        InstallationLayout layout,
        InstallationLayoutLease layoutLease,
        string version,
        string versionPath,
        CurrentPointerSnapshot priorPointer,
        CancellationToken cancellationToken)
    {
        var priorVersion = priorPointer.Version;
        var targetExisted = Directory.Exists(versionPath);
        if (targetExisted && !ValidateExactVersion(package, versionPath))
        {
            return new(
                LocalAiPackageInstallStatus.ImmutableConflict,
                version,
                priorVersion,
                versionPath,
                null,
                "The immutable target version already exists with different content.");
        }

        string? launcherTemporary = null;
        var publishedVersion = false;
        var launcherReplaced = false;
        InstallationLayoutLease.RetainedInstalledVersion? retainedVersion = null;
        InstallationLayoutLease.RetainedLauncherBackup? launcherBackup = null;
        try
        {
            if (!targetExisted)
            {
                using var temporaryVersion = layoutLease.CreateVersionTemporary();
                foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
                {
                    await CopyVerifiedAsync(
                            package,
                            file,
                            Path.Combine(temporaryVersion.CanonicalPath, file),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                ValidateExactDirectory(
                    temporaryVersion.CanonicalPath,
                    LocalAiPackageLayout.VersionRequiredFiles);
                package.Revalidate();
                try
                {
                    temporaryVersion.PublishAbsent(version);
                    publishedVersion = true;
                }
                catch (LocalAiPackageInstallationException) when (Directory.Exists(versionPath))
                {
                    if (!ValidateExactVersion(package, versionPath))
                    {
                        return new(
                            LocalAiPackageInstallStatus.ImmutableConflict,
                            version,
                            priorVersion,
                            versionPath,
                            null,
                            "The immutable target version appeared with different content.");
                    }

                    targetExisted = true;
                    layoutLease.RegisterPublishedVersion(version);
                }

                if (publishedVersion)
                {
                    layoutLease.CommitScaffold();
                }

                if (!ValidateExactVersion(package, versionPath))
                {
                    throw new LocalAiPackageInstallationException("The immutable version failed read-back verification.");
                }
            }

            if (targetExisted)
            {
                layoutLease.CommitScaffold();
            }

            retainedVersion = layoutLease.LockInstalledVersion(
                version,
                LocalAiPackageLayout.VersionRequiredFiles.Select(file => FileMetadata(package, file)));
            retainedVersion.Revalidate();
            package.Revalidate();

            if (File.Exists(layout.LauncherPath))
            {
                launcherBackup = layoutLease.CreateLauncherBackup();
            }
            else
            {
                Directory.CreateDirectory(layout.LauncherDirectory);
                ValidateDirectory(layout.LauncherDirectory);
            }

            launcherTemporary = Path.Combine(
                layout.LauncherDirectory,
                ".launcher-" + Guid.NewGuid().ToString("N") + ".tmp");
            await CopyVerifiedAsync(
                    package,
                    LocalAiPackageLayout.StableLauncherFile,
                    launcherTemporary,
                    cancellationToken)
                .ConfigureAwait(false);
            package.Revalidate();
            File.Move(launcherTemporary, layout.LauncherPath, overwrite: true);
            launcherTemporary = null;
            launcherReplaced = true;
            VerifyFile(layout.LauncherPath, FileMetadata(package, LocalAiPackageLayout.StableLauncherFile));
            package.Revalidate();
            ProcessResult? result = null;
            Exception? activationException = null;
            if (cancellationToken.IsCancellationRequested)
            {
                activationException = new OperationCanceledException(cancellationToken);
            }
            else
            {
                using var trustedLauncher = layoutLease.LockLauncher(
                    FileMetadata(package, LocalAiPackageLayout.StableLauncherFile));
                try
                {
                    retainedVersion.Revalidate();
                    package.Revalidate();
                    trustedLauncher.Revalidate();
                    result = await processRunner.RunAsync(
                            trustedLauncher.CanonicalPath,
                            ActivationArguments(version, priorPointer),
                            activationTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is ProcessTerminationException or OperationCanceledException ||
                    IsNativeHandoffFailure(exception))
                {
                    activationException = exception;
                }
                finally
                {
                    trustedLauncher.Revalidate();
                    retainedVersion.Revalidate();
                    package.Revalidate();
                }
            }

            if (activationException is not null || result is null ||
                result.ExitCode != 0 || result.TimedOut || result.Cancelled ||
                !IsCanonicalPointer(ReadPointerLockedOrNull(layout), version))
            {
                return await RecoverActivationFailureAsync(
                        package,
                        layout,
                        layoutLease,
                        retainedVersion!,
                        version,
                        priorPointer,
                        versionPath,
                        launcherBackup,
                        publishedVersion,
                        targetExisted)
                    .ConfigureAwait(false);
            }

            return new(
                targetExisted
                    ? LocalAiPackageInstallStatus.AlreadyInstalled
                    : LocalAiPackageInstallStatus.Installed,
                version,
                priorVersion,
                versionPath,
                launcherBackup?.Metadata,
                null);
        }
        catch
        {
            if (launcherTemporary is not null && File.Exists(launcherTemporary))
            {
                File.Delete(launcherTemporary);
            }

            if (launcherReplaced)
            {
                return await RecoverActivationFailureAsync(
                        package,
                        layout,
                        layoutLease,
                        retainedVersion!,
                        version,
                        priorPointer,
                        versionPath,
                        launcherBackup,
                        publishedVersion,
                        targetExisted)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            launcherBackup?.Dispose();
            retainedVersion?.Dispose();
        }
    }

    public Task<LocalAiPackageInstallResult> InstallAsync(
        VerifiedPackage package,
        InstallationLayout layout,
        ExistingLocalAiSnapshot diagnosedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosedState);
        // Planning evidence is intentionally not trusted for execution.
        return InstallAsync(package, layout, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private async Task<LocalAiPackageInstallResult> RecoverActivationFailureAsync(
        VerifiedPackage package,
        InstallationLayout layout,
        InstallationLayoutLease layoutLease,
        InstallationLayoutLease.RetainedInstalledVersion retainedVersion,
        string version,
        CurrentPointerSnapshot priorPointer,
        string versionPath,
        InstallationLayoutLease.RetainedLauncherBackup? launcherBackup,
        bool publishedVersion,
        bool targetExisted)
    {
        var priorVersion = priorPointer.Version;
        if (!priorPointer.Exists)
        {
            return RecoverFreshFailureUnderPointerLock(
                layout,
                version,
                versionPath,
                launcherBackup,
                publishedVersion,
                targetExisted);
        }

        var actualPointer = ReadPointerLockedOrNull(layout);

        if (launcherBackup is null)
        {
            return new(
                LocalAiPackageInstallStatus.RollbackFailed,
                version,
                priorVersion,
                versionPath,
                null,
                "The prior stable launcher could not be restored.");
        }

        if (actualPointer is not null && PointersEqual(actualPointer, priorPointer))
        {
            return RestorePriorLauncherUnderPointerLock(
                layout,
                version,
                priorVersion!,
                priorPointer,
                versionPath,
                launcherBackup,
                publishedVersion,
                targetExisted,
                "Activation failed and the prior version remained selected.");
        }

        var expectedNewPointer = CurrentPointerSnapshot.CreateCanonicalBytes(version);
        if (actualPointer is null || !actualPointer.Exists || !actualPointer.IsCanonical ||
            !string.Equals(actualPointer.Version, version, StringComparison.Ordinal) ||
            !actualPointer.RawBytes.SequenceEqual(expectedNewPointer))
        {
            return new(
                LocalAiPackageInstallStatus.Indeterminate,
                version,
                priorVersion,
                versionPath,
                launcherBackup.Metadata,
                "The current-version pointer changed to an unrelated state; no recovery mutation was attempted.",
                publishedVersion && !targetExisted);
        }

        ProcessResult? rollback = null;
        try
        {
            retainedVersion.Revalidate();
            package.Revalidate();
            using var trustedLauncher = layoutLease.LockLauncher(
                FileMetadata(package, LocalAiPackageLayout.StableLauncherFile));
            try
            {
                retainedVersion.Revalidate();
                rollback = await processRunner.RunAsync(
                        trustedLauncher.CanonicalPath,
                        ActivationArguments(priorVersion!, actualPointer),
                        activationTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                trustedLauncher.Revalidate();
                retainedVersion.Revalidate();
                package.Revalidate();
            }
        }
        catch (Exception exception) when (
            exception is ProcessTerminationException or OperationCanceledException or
            LocalAiPackageInstallationException || IsNativeHandoffFailure(exception))
        {
        }

        if (rollback is { ExitCode: 0, TimedOut: false, Cancelled: false })
        {
            return RestorePriorLauncherUnderPointerLock(
                layout,
                version,
                priorVersion!,
                priorPointer,
                versionPath,
                launcherBackup,
                publishedVersion,
                targetExisted,
                "Activation failed and the prior version was restored.");
        }

        var pointerAfterRollbackFailure = ReadPointerLockedOrNull(layout);
        if (pointerAfterRollbackFailure is not null &&
            PointersEqual(pointerAfterRollbackFailure, priorPointer))
        {
            return RestorePriorLauncherUnderPointerLock(
                layout,
                version,
                priorVersion!,
                priorPointer,
                versionPath,
                launcherBackup,
                publishedVersion,
                targetExisted,
                "Rollback selected the prior version despite a process failure; the prior launcher was restored.");
        }

        if (pointerAfterRollbackFailure is null ||
            !pointerAfterRollbackFailure.Exists ||
            !pointerAfterRollbackFailure.IsCanonical ||
            !string.Equals(pointerAfterRollbackFailure.Version, version, StringComparison.Ordinal) ||
            !pointerAfterRollbackFailure.RawBytes.SequenceEqual(
                CurrentPointerSnapshot.CreateCanonicalBytes(version)))
        {
            return new(
                LocalAiPackageInstallStatus.Indeterminate,
                version,
                priorVersion,
                versionPath,
                launcherBackup.Metadata,
                "Rollback failed and the current-version pointer is indeterminate; no launcher mutation was attempted.",
                publishedVersion && !targetExisted);
        }

        return new(
            LocalAiPackageInstallStatus.RollbackFailed,
            version,
            priorVersion,
            versionPath,
            launcherBackup.Metadata,
            "Activation and rollback both failed; manual recovery is required.");
    }

    [SupportedOSPlatform("windows")]
    private LocalAiPackageInstallResult RecoverFreshFailureUnderPointerLock(
        InstallationLayout layout,
        string version,
        string versionPath,
        InstallationLayoutLease.RetainedLauncherBackup? launcherBackup,
        bool publishedVersion,
        bool targetExisted)
    {
        try
        {
            using var pointerLease = ActivationCoordinator.AcquireExclusive(
                layout.BinRoot,
                activationTimeout);
            var actualPointer = CurrentPointerSnapshot.Read(pointerLease);
            if (actualPointer.Exists)
            {
                return Manual();
            }

            if (File.Exists(layout.LauncherPath))
            {
                File.Delete(layout.LauncherPath);
            }

            return new(
                LocalAiPackageInstallStatus.RolledBack,
                version,
                null,
                versionPath,
                launcherBackup?.Metadata,
                "Activation failed before a current version was selected.",
                publishedVersion && !targetExisted);
        }
        catch (Exception exception) when (
            exception is CurrentPointerException or ActivationCoordinationException ||
            IsNativeHandoffFailure(exception))
        {
            return Manual();
        }

        LocalAiPackageInstallResult Manual() => new(
            LocalAiPackageInstallStatus.ManualRecoveryRequired,
            version,
            null,
            versionPath,
            launcherBackup?.Metadata,
            "Activation may have changed the current version; manual recovery is required.",
            publishedVersion && !targetExisted);
    }

    [SupportedOSPlatform("windows")]
    private LocalAiPackageInstallResult RestorePriorLauncherUnderPointerLock(
        InstallationLayout layout,
        string version,
        string priorVersion,
        CurrentPointerSnapshot expectedPointer,
        string versionPath,
        InstallationLayoutLease.RetainedLauncherBackup launcherBackup,
        bool publishedVersion,
        bool targetExisted,
        string reason)
    {
        try
        {
            using var pointerLease = ActivationCoordinator.AcquireExclusive(
                layout.BinRoot,
                activationTimeout);
            var actualPointer = CurrentPointerSnapshot.Read(pointerLease);
            if (!PointersEqual(actualPointer, expectedPointer))
            {
                return new(
                    LocalAiPackageInstallStatus.Indeterminate,
                    version,
                    priorVersion,
                    versionPath,
                    launcherBackup.Metadata,
                    "The current-version pointer changed before launcher recovery; no launcher mutation was attempted.",
                    publishedVersion && !targetExisted);
            }

            return RestorePriorLauncher(
                layout,
                version,
                priorVersion,
                versionPath,
                launcherBackup,
                publishedVersion,
                targetExisted,
                reason);
        }
        catch (Exception exception) when (
            exception is CurrentPointerException or ActivationCoordinationException ||
            IsNativeHandoffFailure(exception))
        {
            return new(
                LocalAiPackageInstallStatus.Indeterminate,
                version,
                priorVersion,
                versionPath,
                launcherBackup.Metadata,
                "The current-version pointer could not be locked for launcher recovery; no launcher mutation was attempted.",
                publishedVersion && !targetExisted);
        }
    }

    [SupportedOSPlatform("windows")]
    private static LocalAiPackageInstallResult RestorePriorLauncher(
        InstallationLayout layout,
        string version,
        string priorVersion,
        string versionPath,
        InstallationLayoutLease.RetainedLauncherBackup launcherBackup,
        bool publishedVersion,
        bool targetExisted,
        string reason)
    {
        try
        {
            RestoreLauncher(layout.LauncherPath, launcherBackup);
            return new(
                LocalAiPackageInstallStatus.RolledBack,
                version,
                priorVersion,
                versionPath,
                launcherBackup.Metadata,
                reason,
                publishedVersion && !targetExisted);
        }
        catch (Exception exception) when (
            exception is LocalAiPackageInstallationException || IsNativeHandoffFailure(exception))
        {
            return new(
                LocalAiPackageInstallStatus.RollbackFailed,
                version,
                priorVersion,
                versionPath,
                launcherBackup.Metadata,
                "The prior stable launcher could not be restored.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreLauncher(
        string launcherPath,
        InstallationLayoutLease.RetainedLauncherBackup backup)
    {
        backup.Revalidate();
        var temporary = Path.Combine(
            Path.GetDirectoryName(launcherPath)!,
            ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var source = backup.OpenReadDuplicate())
            using (var destination = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                source.Position = 0;
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            backup.Revalidate();
            File.Move(temporary, launcherPath, overwrite: true);
            VerifyBackupCopy(launcherPath, backup.Metadata);
            backup.Revalidate();
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void VerifyBackupCopy(string path, LauncherBackupMetadata expected)
    {
        ValidateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != expected.Length ||
            !string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                expected.Sha256,
                StringComparison.Ordinal))
        {
            throw new LocalAiPackageInstallationException(
                "The prior stable launcher failed read-back verification.");
        }
    }

    private static bool ValidateExactVersion(VerifiedPackage package, string versionPath)
    {
        try
        {
            ValidateExactDirectory(versionPath, LocalAiPackageLayout.VersionRequiredFiles);
            foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
            {
                VerifyFile(Path.Combine(versionPath, file), FileMetadata(package, file));
            }

            package.Revalidate();
            return true;
        }
        catch (LocalAiPackageInstallationException)
        {
            return false;
        }
    }

    private static void ValidateRecognizedLayout(InstallationLayout layout)
    {
        ValidateDirectory(layout.Root);
        ValidateDirectory(layout.BinRoot);
        ValidateDirectory(layout.VersionsRoot);
        ValidateAllowedNames(layout.Root, ["bin"]);
        ValidateAllowedNames(
            layout.BinRoot,
            ["versions", "launcher", "installer-backups", "current.json", "current.lock"]);
        foreach (var versionDirectory in Directory.EnumerateDirectories(layout.VersionsRoot))
        {
            ValidateDirectory(versionDirectory);
            ValidateExactDirectory(versionDirectory, LocalAiPackageLayout.VersionRequiredFiles);
        }

        if (Directory.Exists(layout.LauncherDirectory))
        {
            ValidateDirectory(layout.LauncherDirectory);
            ValidateAllowedNames(layout.LauncherDirectory, [LocalAiPackageLayout.StableLauncherFile]);
            if (File.Exists(layout.LauncherPath))
            {
                ValidateFile(layout.LauncherPath);
            }
        }

        if (Directory.Exists(layout.InstallerBackupsRoot))
        {
            ValidateDirectory(layout.InstallerBackupsRoot);
            foreach (var backupDirectory in Directory.EnumerateFileSystemEntries(layout.InstallerBackupsRoot))
            {
                if (!Directory.Exists(backupDirectory) ||
                    !Path.GetFileName(backupDirectory).StartsWith("launcher-", StringComparison.Ordinal))
                {
                    throw new LocalAiPackageInstallationException("The installer backup layout is invalid.");
                }

                ValidateExactDirectory(backupDirectory, [LocalAiPackageLayout.StableLauncherFile]);
            }
        }

        if (File.Exists(layout.CurrentPointerPath))
        {
            ValidateFile(layout.CurrentPointerPath);
        }

        var currentLock = Path.Combine(layout.BinRoot, "current.lock");
        if (File.Exists(currentLock))
        {
            ValidateFile(currentLock);
        }
    }

    private static void ValidateAllowedNames(string directory, IEnumerable<string> allowed)
    {
        var expected = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!expected.Contains(Path.GetFileName(entry)))
            {
                throw new LocalAiPackageInstallationException("The installation layout contains an unexpected entry.");
            }
        }
    }

    private static void ValidateExactDirectory(string directory, IEnumerable<string> requiredFiles)
    {
        ValidateDirectory(directory);
        var required = new HashSet<string>(requiredFiles, StringComparer.Ordinal);
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (entries.Length != required.Count)
        {
            throw new LocalAiPackageInstallationException("The immutable version layout is invalid.");
        }

        foreach (var entry in entries)
        {
            if (!required.Remove(Path.GetFileName(entry)))
            {
                throw new LocalAiPackageInstallationException("The immutable version layout is invalid.");
            }

            ValidateFile(entry);
        }
    }

    private static void ValidateFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new LocalAiPackageInstallationException("The installation layout is unsafe.");
        }
    }

    private static void ValidatePackageContract(VerifiedPackage package)
    {
        if (package.Manifest.ProtocolVersion != BrokerCompatibilityContract.ProtocolVersion ||
            !string.Equals(package.Manifest.BuildCompatibilityId, BrokerCompatibilityContract.BuildCompatibilityId, StringComparison.Ordinal) ||
            !IsSafeVersion(package.Manifest.VersionDirectory))
        {
            throw new LocalAiPackageInstallationException("The verified package contract is incompatible.");
        }

        var expected = new HashSet<string>(
            LocalAiPackageLayout.PackageArtifactFiles.Append(ReleasePackageVerifier.PackageMetadataFileName),
            StringComparer.Ordinal);
        if (!expected.SetEquals(package.Files.Select(file => file.RelativePath)))
        {
            throw new LocalAiPackageInstallationException("The verified package allowlist is invalid.");
        }
    }

    private static bool IsSafeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128 ||
            version is "." or ".." ||
            !IsAsciiAlphaNumeric(version[0]) || !IsAsciiAlphaNumeric(version[^1]) ||
            ReservedVersionNames.Contains(version.Split('.')[0]))
        {
            return false;
        }

        return version.All(character =>
            IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static void EnsureFreshLayout(InstallationLayout layout)
    {
        if (Directory.Exists(layout.Root) || File.Exists(layout.Root))
        {
            throw new LocalAiPackageInstallationException("The existing LocalAi layout is unrecognized.");
        }

        Directory.CreateDirectory(layout.VersionsRoot);
        ValidateDirectory(layout.Root);
        ValidateDirectory(layout.BinRoot);
        ValidateDirectory(layout.VersionsRoot);
    }

    private static string CreateUniqueDirectory(string parent, string prefix)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Path.Combine(parent, prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".tmp");
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate);
            ValidateDirectory(candidate);
            return candidate;
        }

        throw new LocalAiPackageInstallationException("A secure installer workspace could not be created.");
    }

    private static async Task CopyVerifiedAsync(
        VerifiedPackage package,
        string relativePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var metadata = FileMetadata(package, relativePath);
        await using var source = package.OpenRead(relativePath);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length = checked(length + read);
            if (length > metadata.Length)
            {
                throw new LocalAiPackageInstallationException("Verified package content changed during installation.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (length != metadata.Length || !string.Equals(actual, metadata.Sha256, StringComparison.Ordinal))
        {
            throw new LocalAiPackageInstallationException("Verified package content changed during installation.");
        }

        package.Revalidate();
    }

    private static VerifiedPackageFile FileMetadata(VerifiedPackage package, string relativePath) =>
        package.Files.Single(file => string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal));

    private static void VerifyFile(string path, VerifiedPackageFile metadata)
    {
        ValidateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        WindowsStagingRootLease.FileIdentity? identity = null;
        if (OperatingSystem.IsWindows())
        {
            identity = WindowsStagingRootLease.GetIdentity(stream.SafeFileHandle);
            if (identity.Value.Attributes.HasFlag(FileAttributes.Directory) ||
                identity.Value.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !string.Equals(
                    WindowsStagingRootLease.GetFinalPath(stream.SafeFileHandle),
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalAiPackageInstallationException("An installed file failed identity verification.");
            }
        }

        if (stream.Length != metadata.Length ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), metadata.Sha256, StringComparison.Ordinal))
        {
            throw new LocalAiPackageInstallationException("An installed file failed read-back verification.");
        }

        if (OperatingSystem.IsWindows() && identity is { } expectedIdentity &&
            (WindowsStagingRootLease.GetIdentity(stream.SafeFileHandle) != expectedIdentity ||
             !string.Equals(
                 WindowsStagingRootLease.GetFinalPath(stream.SafeFileHandle),
                 Path.GetFullPath(path),
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new LocalAiPackageInstallationException("An installed file changed during verification.");
        }
    }

    private static void ValidateDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new LocalAiPackageInstallationException("The installation layout is unsafe.");
        }
    }

    private CurrentPointerSnapshot ReadPointerLocked(InstallationLayout layout)
    {
        using var activationLease = ActivationCoordinator.AcquireExclusive(
            layout.BinRoot,
            activationTimeout);
        return CurrentPointerSnapshot.Read(activationLease);
    }

    private CurrentPointerSnapshot? ReadPointerLockedOrNull(InstallationLayout layout)
    {
        try
        {
            return ReadPointerLocked(layout);
        }
        catch (Exception exception) when (
            exception is CurrentPointerException or CurrentPointerChangedException or
            ActivationCoordinationException || IsNativeHandoffFailure(exception))
        {
            return null;
        }
    }

    private static bool IsNativeHandoffFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException;

    private static IReadOnlyList<string> ActivationArguments(
        string version,
        CurrentPointerSnapshot expected) =>
        expected.Exists
            ? [
                "activate", version, "--stop-running", "--if-current-sha256",
                expected.Sha256Hex,
            ]
            : ["activate", version, "--stop-running", "--if-current-missing"];

    private static bool IsCanonicalPointer(
        CurrentPointerSnapshot? pointer,
        string version) =>
        pointer is { Exists: true, IsCanonical: true } &&
        string.Equals(pointer.Version, version, StringComparison.Ordinal) &&
        pointer.RawBytes.SequenceEqual(CurrentPointerSnapshot.CreateCanonicalBytes(version));

    private static bool PointersEqual(
        CurrentPointerSnapshot left,
        CurrentPointerSnapshot right) =>
        left.Exists == right.Exists &&
        left.RawBytes.SequenceEqual(right.RawBytes);
}

public sealed class LocalAiPackageInstallationException(string message) : Exception(message);
