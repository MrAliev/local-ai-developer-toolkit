using System.Security.Cryptography;
using System.Runtime.Versioning;
using LocalAi.Contracts;
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

    public LocalAiPackageInstaller(
        IProcessRunner processRunner,
        IExistingLocalAiInspector inspector,
        TimeSpan activationTimeout)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        if (activationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activationTimeout));
        }

        this.activationTimeout = activationTimeout;
    }

    public async Task<LocalAiPackageInstallResult> InstallAsync(
        VerifiedPackage package,
        InstallationLayout layout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await InstallCoreAsync(package, layout, cancellationToken)
                .ConfigureAwait(false);
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

        var priorPointer = CapturePointer(layout.CurrentPointerPath);
        if ((existing.State == ExistingLocalAiState.Compatible &&
             (!priorPointer.Valid || !string.Equals(priorPointer.Version, existing.Version, StringComparison.Ordinal))) ||
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

        var priorVersion = priorPointer.Version;

        package.Revalidate();
        InstallationLayoutLease layoutLease;
        try
        {
            layoutLease = InstallationLayoutLease.Acquire(layout);
        }
        catch (LocalAiPackageInstallationException)
        {
            return new(
                LocalAiPackageInstallStatus.Refused,
                version,
                priorVersion,
                versionPath,
                null,
                "The existing LocalAi layout is unrecognized.");
        }

        using (layoutLease)
        {
            return await InstallUnderLayoutLeaseAsync(
                    package,
                    layout,
                    layoutLease,
                    version,
                    versionPath,
                    priorVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<LocalAiPackageInstallResult> InstallUnderLayoutLeaseAsync(
        VerifiedPackage package,
        InstallationLayout layout,
        InstallationLayoutLease layoutLease,
        string version,
        string versionPath,
        string? priorVersion,
        CancellationToken cancellationToken)
    {

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
        LauncherBackupMetadata? launcherBackup = null;
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

                if (!ValidateExactVersion(package, versionPath))
                {
                    throw new LocalAiPackageInstallationException("The immutable version failed read-back verification.");
                }
            }

            if (File.Exists(layout.LauncherPath))
            {
                launcherBackup = BackupLauncher(layout);
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
                    trustedLauncher.Revalidate();
                    result = await processRunner.RunAsync(
                            trustedLauncher.CanonicalPath,
                            ["activate", version, "--stop-running"],
                            activationTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is ProcessTerminationException or OperationCanceledException)
                {
                    activationException = exception;
                }
                finally
                {
                    trustedLauncher.Revalidate();
                }
            }

            if (activationException is not null || result is null ||
                result.ExitCode != 0 || result.TimedOut || result.Cancelled ||
                !string.Equals(ReadPointer(layout.CurrentPointerPath), version, StringComparison.Ordinal))
            {
                return await RecoverActivationFailureAsync(
                        layout,
                        version,
                        priorVersion,
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
                launcherBackup,
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
                        layout,
                        version,
                        priorVersion,
                        versionPath,
                        launcherBackup,
                        publishedVersion,
                        targetExisted)
                    .ConfigureAwait(false);
            }

            throw;
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

    private async Task<LocalAiPackageInstallResult> RecoverActivationFailureAsync(
        InstallationLayout layout,
        string version,
        string? priorVersion,
        string versionPath,
        LauncherBackupMetadata? launcherBackup,
        bool publishedVersion,
        bool targetExisted)
    {
        var actualVersion = ReadPointer(layout.CurrentPointerPath);
        if (priorVersion is null)
        {
            if (File.Exists(layout.CurrentPointerPath))
            {
                return new(
                    LocalAiPackageInstallStatus.ManualRecoveryRequired,
                    version,
                    null,
                    versionPath,
                    launcherBackup,
                    "Activation may have changed the current version; manual recovery is required.",
                    publishedVersion && !targetExisted);
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
                launcherBackup,
                "Activation failed before a current version was selected.",
                publishedVersion && !targetExisted);
        }

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

        try
        {
            RestoreLauncher(layout.LauncherPath, launcherBackup.Path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or LocalAiPackageInstallationException)
        {
            return new(
                LocalAiPackageInstallStatus.RollbackFailed,
                version,
                priorVersion,
                versionPath,
                launcherBackup,
                "The prior stable launcher could not be restored.");
        }

        if (string.Equals(actualVersion, priorVersion, StringComparison.Ordinal))
        {
            return new(
                LocalAiPackageInstallStatus.RolledBack,
                version,
                priorVersion,
                versionPath,
                launcherBackup,
                "Activation failed and the prior version remained selected.",
                publishedVersion && !targetExisted);
        }

        ProcessResult? rollback = null;
        try
        {
            ValidateFile(layout.LauncherPath);
            rollback = await processRunner.RunAsync(
                    layout.LauncherPath,
                    ["activate", priorVersion, "--stop-running"],
                    activationTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ProcessTerminationException or OperationCanceledException)
        {
        }

        if (rollback is { ExitCode: 0, TimedOut: false, Cancelled: false } &&
            string.Equals(ReadPointer(layout.CurrentPointerPath), priorVersion, StringComparison.Ordinal))
        {
            return new(
                LocalAiPackageInstallStatus.RolledBack,
                version,
                priorVersion,
                versionPath,
                launcherBackup,
                "Activation failed and the prior version was restored.",
                publishedVersion && !targetExisted);
        }

        return new(
            LocalAiPackageInstallStatus.RollbackFailed,
            version,
            priorVersion,
            versionPath,
            launcherBackup,
            "Activation and rollback both failed; manual recovery is required.");
    }

    private static void RestoreLauncher(string launcherPath, string backupPath)
    {
        ValidateFile(backupPath);
        var temporary = Path.Combine(
            Path.GetDirectoryName(launcherPath)!,
            ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CopyExistingExact(backupPath, temporary);
            File.Move(temporary, launcherPath, overwrite: true);
            if (!FilesEqual(backupPath, launcherPath))
            {
                throw new LocalAiPackageInstallationException("The prior stable launcher failed read-back verification.");
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool FilesEqual(string leftPath, string rightPath)
    {
        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return left.Length == right.Length &&
               CryptographicOperations.FixedTimeEquals(SHA256.HashData(left), SHA256.HashData(right));
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

    private static LauncherBackupMetadata BackupLauncher(InstallationLayout layout)
    {
        ValidateFile(layout.LauncherPath);
        Directory.CreateDirectory(layout.InstallerBackupsRoot);
        ValidateDirectory(layout.InstallerBackupsRoot);
        var backupDirectory = CreateUniqueDirectory(layout.InstallerBackupsRoot, "launcher-");
        var backupPath = Path.Combine(backupDirectory, LocalAiPackageLayout.StableLauncherFile);
        CopyExistingExact(layout.LauncherPath, backupPath);
        using var backup = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(
            backupPath,
            backup.Length,
            Convert.ToHexString(SHA256.HashData(backup)));
    }

    private static void CopyExistingExact(string sourcePath, string destinationPath)
    {
        byte[] sourceHash;
        long sourceLength;
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (var destination = new FileStream(
                       destinationPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            sourceLength = source.Length;
            source.Position = 0;
            sourceHash = SHA256.HashData(source);
        }

        using var readBack = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (sourceLength != readBack.Length ||
            !CryptographicOperations.FixedTimeEquals(sourceHash, SHA256.HashData(readBack)))
        {
            throw new LocalAiPackageInstallationException("The stable launcher backup failed verification.");
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

    private static PointerSnapshot CapturePointer(string path)
    {
        if (!File.Exists(path))
        {
            return new(false, true, null, Array.Empty<byte>());
        }

        byte[] bytes = [];
        try
        {
            bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                return new(true, false, null, bytes);
            }

            using var document = System.Text.Json.JsonDocument.Parse(bytes, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return new(true, false, null, bytes);
            }

            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 2 || names.Distinct(StringComparer.Ordinal).Count() != 2 ||
                !names.Contains("schemaVersion", StringComparer.Ordinal) ||
                !names.Contains("version", StringComparer.Ordinal) ||
                root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("version").ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return new(true, false, null, bytes);
            }

            var version = root.GetProperty("version").GetString();
            return IsSafeVersion(version)
                ? new(true, true, version, bytes)
                : new(true, false, null, bytes);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
            InvalidOperationException or FormatException or OverflowException)
        {
            return new(true, false, null, bytes);
        }
    }

    private static string? ReadPointer(string path)
    {
        var snapshot = CapturePointer(path);
        return snapshot.Valid ? snapshot.Version : null;
    }

    private sealed record PointerSnapshot(
        bool Exists,
        bool Valid,
        string? Version,
        IReadOnlyList<byte> Bytes);
}

public sealed class LocalAiPackageInstallationException(string message) : Exception(message);
