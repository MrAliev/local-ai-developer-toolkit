using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Releases;
using Microsoft.Win32.SafeHandles;

namespace LocalAi.Installer.Core.Activation;

[SupportedOSPlatform("windows")]
public sealed class InstallationLayoutLease : IDisposable
{
    private readonly List<DirectoryEvidence> directories;
    private readonly List<VersionTemporary> versionTemporaries = [];
    private readonly VersionDirectoryPublisher publishVersionDirectory;
    private bool scaffoldCommitted;
    private bool disposed;

    private InstallationLayoutLease(
        InstallationLayout layout,
        List<DirectoryEvidence> directories,
        VersionDirectoryPublisher publishVersionDirectory)
    {
        Layout = layout;
        this.directories = directories;
        this.publishVersionDirectory = publishVersionDirectory;
    }

    public InstallationLayout Layout { get; }

    public void CommitScaffold()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Revalidate();
        scaffoldCommitted = true;
    }

    public VersionTemporary CreateVersionTemporary()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Revalidate();
        var versions = EvidenceFor(Layout.VersionsRoot);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = ".install-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".tmp";
            var path = Path.Combine(Layout.VersionsRoot, name);
            try
            {
                var handle = NativeDirectory.CreateExclusive(versions.Handle, name, path);
                var temporary = new VersionTemporary(this, handle, path);
                versionTemporaries.Add(temporary);
                Revalidate();
                return temporary;
            }
            catch (LocalAiPackageInstallationException) when (Directory.Exists(path) || File.Exists(path))
            {
            }
        }

        throw Failure();
    }

    public TrustedLauncher LockLauncher(VerifiedPackageFile expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ObjectDisposedException.ThrowIf(disposed, this);
        Revalidate();
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                Layout.LauncherPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.RandomAccess);
            var result = new TrustedLauncher(this, stream, Layout.LauncherPath, expected);
            stream = null;
            result.Revalidate();
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public RetainedLauncherBackup CreateLauncherBackup()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Revalidate();
        using var source = new FileStream(
            Layout.LauncherPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.RandomAccess);
        ValidateFileHandle(source, Layout.LauncherPath);

        var backups = EvidenceFor(Layout.InstallerBackupsRoot);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = "launcher-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var directoryPath = Path.Combine(Layout.InstallerBackupsRoot, name);
            try
            {
                var handle = NativeDirectory.CreateExclusive(backups.Handle, name, directoryPath);
                var evidence = Evidence(handle, directoryPath);
                directories.Add(evidence);
                var backupPath = Path.Combine(directoryPath, LocalAiPackageLayout.StableLauncherFile);
                try
                {
                    byte[] sourceHash;
                    long sourceLength;
                    using (var destination = new FileStream(
                               backupPath,
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

                    sourceLength = source.Length;
                    source.Position = 0;
                    sourceHash = SHA256.HashData(source);
                    ValidateFileHandle(source, Layout.LauncherPath);
                    var metadata = new LauncherBackupMetadata(
                        backupPath,
                        sourceLength,
                        Convert.ToHexString(sourceHash));
                    var retained = RetainLauncherBackup(backupPath, metadata);
                    Revalidate();
                    return retained;
                }
                catch
                {
                    try
                    {
                        if (File.Exists(backupPath))
                        {
                            File.Delete(backupPath);
                        }

                        NativeDirectory.DeleteEmpty(evidence.Handle);
                    }
                    catch (Exception cleanupException) when (
                        cleanupException is IOException or UnauthorizedAccessException or Win32Exception)
                    {
                    }

                    directories.Remove(evidence);
                    evidence.Handle.Dispose();
                    throw;
                }
            }
            catch (LocalAiPackageInstallationException) when (
                Directory.Exists(directoryPath) || File.Exists(directoryPath))
            {
            }
        }

        throw Failure();
    }

    internal RetainedLauncherBackup RetainLauncherBackup(
        string path,
        LauncherBackupMetadata expected)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(expected);
        var canonicalPath = Path.GetFullPath(path);
        if (!string.Equals(canonicalPath, Path.GetFullPath(expected.Path), StringComparison.OrdinalIgnoreCase) ||
            !IsBelow(canonicalPath, Layout.InstallerBackupsRoot) ||
            !string.Equals(Path.GetFileName(canonicalPath), LocalAiPackageLayout.StableLauncherFile, StringComparison.Ordinal) ||
            !directories.Any(directory => string.Equals(
                directory.CanonicalPath,
                Path.GetDirectoryName(canonicalPath),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw Failure();
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                canonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.RandomAccess);
            var retained = new RetainedLauncherBackup(this, stream, canonicalPath, expected);
            try
            {
                retained.Revalidate();
                stream = null;
                return retained;
            }
            catch
            {
                retained.Dispose();
                stream = null;
                throw;
            }
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public RetainedInstalledVersion LockInstalledVersion(
        string version,
        IEnumerable<VerifiedPackageFile> expectedFiles)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!LocalAiVersionName.IsSafe(version))
        {
            throw Failure();
        }

        var expected = expectedFiles.ToDictionary(
            file => file.RelativePath,
            StringComparer.Ordinal);
        if (!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
                LocalAiPackageLayout.VersionRequiredFiles))
        {
            throw Failure();
        }

        Revalidate();
        var versionPath = Path.Combine(Layout.VersionsRoot, version);
        if (!directories.Any(directory => string.Equals(
                directory.CanonicalPath,
                versionPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw Failure();
        }

        var files = new List<RetainedInstalledVersion.FileEvidence>();
        try
        {
            foreach (var name in LocalAiPackageLayout.VersionRequiredFiles)
            {
                var path = Path.Combine(versionPath, name);
                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.RandomAccess);
                try
                {
                    ValidateFileHandle(stream, path);
                    files.Add(new(
                        stream,
                        path,
                        WindowsStagingRootLease.GetIdentity(stream.SafeFileHandle),
                        expected[name]));
                    stream = null!;
                }
                finally
                {
                    stream?.Dispose();
                }
            }

            var retained = new RetainedInstalledVersion(this, versionPath, files);
            try
            {
                retained.Revalidate();
                files = null!;
                return retained;
            }
            catch
            {
                retained.Dispose();
                files = null!;
                throw;
            }
        }
        finally
        {
            if (files is not null)
            {
                foreach (var file in files)
                {
                    file.Stream.Dispose();
                }
            }
        }
    }

    public static InstallationLayoutLease Acquire(
        InstallationLayout layout,
        bool requireFreshInstallerTree = false) =>
        Acquire(layout, requireFreshInstallerTree, NativeDirectory.Rename);

    internal static InstallationLayoutLease Acquire(
        InstallationLayout layout,
        bool requireFreshInstallerTree,
        VersionDirectoryPublisher publishVersionDirectory)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(publishVersionDirectory);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Protected LocalAi installation layout is available only on Windows.");
        }

        var directories = new List<DirectoryEvidence>();
        try
        {
            var localAppData = NativeDirectory.OpenAbsolute(layout.LocalAppData);
            directories.Add(Evidence(localAppData, layout.LocalAppData, validateAcl: false));
            var root = NativeDirectory.OpenOrCreate(
                localAppData,
                "LocalAi",
                layout.Root);
            directories.Add(Evidence(root.Handle, layout.Root, created: root.Created));
            ValidateRootShape(layout.Root);
            var bin = NativeDirectory.OpenOrCreate(root.Handle, "bin", layout.BinRoot);
            directories.Add(Evidence(bin.Handle, layout.BinRoot, created: bin.Created));
            if (requireFreshInstallerTree && !bin.Created)
            {
                throw Failure();
            }

            ValidateBinNames(layout.BinRoot);

            var versions = NativeDirectory.OpenOrCreate(
                bin.Handle,
                "versions",
                layout.VersionsRoot);
            directories.Add(Evidence(versions.Handle, layout.VersionsRoot, created: versions.Created));
            if (requireFreshInstallerTree && !versions.Created)
            {
                throw Failure();
            }

            var launcher = NativeDirectory.OpenOrCreate(
                bin.Handle,
                "launcher",
                layout.LauncherDirectory);
            directories.Add(Evidence(launcher.Handle, layout.LauncherDirectory, created: launcher.Created));
            if (requireFreshInstallerTree && !launcher.Created)
            {
                throw Failure();
            }

            var installer = NativeDirectory.OpenOrCreate(
                root.Handle,
                "installer",
                layout.InstallerDirectory);
            directories.Add(Evidence(installer.Handle, layout.InstallerDirectory, created: installer.Created));
            ValidateExactNames(layout.InstallerDirectory, ["backups", "transaction.lock"]);
            var backups = NativeDirectory.OpenOrCreate(
                installer.Handle,
                "backups",
                layout.InstallerBackupsRoot);
            directories.Add(Evidence(backups.Handle, layout.InstallerBackupsRoot, created: backups.Created));

            foreach (var entry in Directory.EnumerateFileSystemEntries(layout.InstallerBackupsRoot))
            {
                var name = Path.GetFileName(entry);
                if (!Directory.Exists(entry) ||
                    !name.StartsWith("launcher-", StringComparison.Ordinal) ||
                    name.Length == "launcher-".Length)
                {
                    throw Failure();
                }

                ValidateExactNames(entry, [LocalAiPackageLayout.StableLauncherFile]);
                ValidateRequiredFile(Path.Combine(entry, LocalAiPackageLayout.StableLauncherFile));
                var backupHandle = NativeDirectory.OpenExisting(backups.Handle, name, entry);
                directories.Add(Evidence(backupHandle, entry));
            }

            ValidateBinShape(layout);
            ValidateInstallerShape(layout);
            foreach (var entry in Directory.EnumerateFileSystemEntries(layout.VersionsRoot))
            {
                var name = Path.GetFileName(entry);
                if (!Directory.Exists(entry) || !LocalAiVersionName.IsSafe(name))
                {
                    throw Failure();
                }

                ValidateExistingVersionDirectory(entry);

                var versionHandle = NativeDirectory.OpenExisting(
                    versions.Handle,
                    name,
                    entry);
                directories.Add(Evidence(versionHandle, entry));
            }

            var result = new InstallationLayoutLease(
                layout,
                directories,
                publishVersionDirectory);
            result.Revalidate();
            directories = null!;
            return result;
        }
        catch (LocalAiPackageInstallationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            Win32Exception or System.Security.SecurityException or
            ArgumentException or NotSupportedException)
        {
            throw Failure();
        }
        finally
        {
            if (directories is not null)
            {
                DisposeDirectories(directories, removeCreatedScaffold: true);
            }
        }
    }

    public void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var directory in directories)
        {
            var identity = WindowsStagingRootLease.GetIdentity(directory.Handle);
            if (identity != directory.Identity ||
                identity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !identity.Attributes.HasFlag(FileAttributes.Directory) ||
                !string.Equals(
                    WindowsStagingRootLease.GetFinalPath(directory.Handle),
                    directory.CanonicalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Failure();
            }

            if (directory.ValidateAcl)
            {
                ValidateDirectoryAcl(directory.CanonicalPath);
            }
        }

        ValidateRootShape(Layout.Root);
        ValidateBinShape(Layout);
        ValidateInstallerShape(Layout);
        var expectedVersions = directories
            .Where(directory => IsBelow(directory.CanonicalPath, Layout.VersionsRoot))
            .Select(directory => Path.GetFileName(directory.CanonicalPath))
            .Concat(versionTemporaries.Select(temporary => Path.GetFileName(temporary.CanonicalPath)))
            .ToHashSet(StringComparer.Ordinal);
        var actualVersions = Directory.EnumerateFileSystemEntries(Layout.VersionsRoot)
            .Select(path => Path.GetFileName(path)!)
            .ToArray();
        if (!expectedVersions.SetEquals(actualVersions))
        {
            throw Failure();
        }

        var expectedBackups = directories
            .Where(directory => IsBelow(directory.CanonicalPath, Layout.InstallerBackupsRoot))
            .Select(directory => Path.GetFileName(directory.CanonicalPath))
            .ToHashSet(StringComparer.Ordinal);
        var actualBackups = Directory.EnumerateFileSystemEntries(Layout.InstallerBackupsRoot)
            .Select(path => Path.GetFileName(path)!)
            .ToArray();
        if (!expectedBackups.SetEquals(actualBackups))
        {
            throw Failure();
        }

        foreach (var directory in directories.Where(directory =>
                     IsBelow(directory.CanonicalPath, Layout.VersionsRoot)))
        {
            ValidateExistingVersionDirectory(directory.CanonicalPath);
        }


        foreach (var directory in directories.Where(directory =>
                     IsBelow(directory.CanonicalPath, Layout.InstallerBackupsRoot)))
        {
            ValidateExactNames(directory.CanonicalPath, [LocalAiPackageLayout.StableLauncherFile]);
            ValidateRequiredFile(Path.Combine(
                directory.CanonicalPath,
                LocalAiPackageLayout.StableLauncherFile));
        }
    }

    public void RegisterPublishedVersion(string version)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!LocalAiVersionName.IsSafe(version))
        {
            throw Failure();
        }

        var versions = directories.Single(directory =>
            string.Equals(
                directory.CanonicalPath,
                Layout.VersionsRoot,
                StringComparison.OrdinalIgnoreCase));
        var expectedPath = Path.Combine(Layout.VersionsRoot, version);
        var handle = NativeDirectory.OpenExisting(
            versions.Handle,
            version,
            expectedPath);
        try
        {
            directories.Add(Evidence(handle, expectedPath));
            handle = null!;
            Revalidate();
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private DirectoryEvidence EvidenceFor(string canonicalPath) =>
        directories.Single(directory => string.Equals(
            directory.CanonicalPath,
            canonicalPath,
            StringComparison.OrdinalIgnoreCase));

    private void Publish(VersionTemporary temporary, string version)
    {
        if (!versionTemporaries.Contains(temporary) || !LocalAiVersionName.IsSafe(version))
        {
            throw Failure();
        }

        Revalidate();
        ValidateExactVersionDirectory(
            temporary.CanonicalPath,
            LocalAiPackageLayout.VersionRequiredFiles);
        var targetPath = Path.Combine(Layout.VersionsRoot, version);
        publishVersionDirectory(
            temporary.Handle,
            EvidenceFor(Layout.VersionsRoot).Handle,
            version);
        temporary.MarkPublished(targetPath);
        versionTemporaries.Remove(temporary);
        directories.Add(Evidence(temporary.Handle, targetPath));
        Revalidate();
    }

    private void Cleanup(VersionTemporary temporary)
    {
        if (!versionTemporaries.Contains(temporary))
        {
            return;
        }

        temporary.Revalidate();
        foreach (var entry in Directory.EnumerateFileSystemEntries(temporary.CanonicalPath))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            File.Delete(entry);
        }

        temporary.Revalidate();
        NativeDirectory.DeleteEmpty(temporary.Handle);
        versionTemporaries.Remove(temporary);
    }

    private void Unregister(VersionTemporary temporary) =>
        versionTemporaries.Remove(temporary);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Exception? cleanupFailure = null;
        foreach (var temporary in versionTemporaries.ToArray())
        {
            try
            {
                temporary.Dispose();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or Win32Exception or
                    LocalAiPackageInstallationException)
            {
                cleanupFailure ??= exception;
            }
        }

        disposed = true;
        if (!scaffoldCommitted)
        {
            DeleteFreshCoordinationFile();
        }

        DisposeDirectories(directories, removeCreatedScaffold: !scaffoldCommitted);

        if (cleanupFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(cleanupFailure)
                .Throw();
        }
    }

    private static void ValidateExactVersionDirectory(
        string directory,
        IEnumerable<string> requiredFiles)
    {
        ValidateDirectoryPath(directory);
        var required = requiredFiles.ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!required.Remove(Path.GetFileName(entry)))
            {
                throw Failure();
            }

            ValidateOptionalFile(entry);
        }

        if (required.Count != 0)
        {
            throw Failure();
        }
    }

    private static void ValidateExistingVersionDirectory(string directory)
    {
        ValidateDirectoryPath(directory);
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        var names = entries
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        var runtimeFiles = LocalAiPackageLayout.VersionRequiredFiles
            .ToHashSet(StringComparer.Ordinal);
        var expandedPackageFiles = LocalAiPackageLayout.PackageArtifactFiles
            .ToHashSet(StringComparer.Ordinal);

        // Older repair and pre-release builds sometimes published the verified stable
        // launcher beside the runtime executables. The versioned launcher is never executed;
        // the trusted copy under bin\launcher remains the only activation entry point. Accept
        // that one known expanded package shape, while continuing to reject every other extra.
        if (!names.SetEquals(runtimeFiles) && !names.SetEquals(expandedPackageFiles))
        {
            throw Failure();
        }

        foreach (var entry in entries)
        {
            ValidateOptionalFile(entry);
        }
    }

    private void DeleteFreshCoordinationFile()
    {
        var bin = directories.Single(directory =>
            string.Equals(
                directory.CanonicalPath,
                Path.GetFullPath(Layout.BinRoot),
                StringComparison.OrdinalIgnoreCase));
        if (!bin.Created)
        {
            return;
        }

        var path = Path.Combine(Layout.BinRoot, "current.lock");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            NativeDirectory.DeleteFile(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or
                System.Security.SecurityException or LocalAiPackageInstallationException)
        {
        }
    }

    private static DirectoryEvidence Evidence(
        SafeFileHandle handle,
        string expectedPath,
        bool validateAcl = true,
        bool created = false)
    {
        WindowsStagingRootLease.ValidateDirectoryHandle(handle, expectedPath);
        var identity = WindowsStagingRootLease.GetIdentity(handle);
        if (identity.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            handle.Dispose();
            throw Failure();
        }

        return new(
            handle,
            identity,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
            validateAcl,
            created);
    }

    private static void ValidateBinNames(string binRoot)
    {
        if (!Directory.Exists(binRoot))
        {
            throw Failure();
        }

        var allowed = new HashSet<string>(
            ["versions", "launcher", "current.json", "current.lock"],
            StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(binRoot))
        {
            if (!allowed.Contains(Path.GetFileName(entry)))
            {
                throw Failure();
            }
        }
    }

    private static void ValidateBinShape(InstallationLayout layout)
    {
        ValidateExactNames(
            layout.BinRoot,
            ["versions", "launcher", "current.json", "current.lock"]);
        ValidateDirectoryPath(layout.VersionsRoot);
        ValidateDirectoryPath(layout.LauncherDirectory);
        ValidateOptionalFile(layout.CurrentPointerPath);
        ValidateOptionalFile(Path.Combine(layout.BinRoot, "current.lock"));
    }

    /// <summary>
    /// Validates only what the installer owns inside the root.
    ///
    /// The root is shared with the runtime: the broker keeps its queue, telemetry and state
    /// here, including loose files such as host.json, policy.json, sequence.json and
    /// broker.lock. Demanding that every entry be a directory refused installation on any
    /// machine where LocalAi had ever run — that is, on every upgrade. Entries the installer
    /// neither reads nor writes are none of its business, and the paths it does resolve are
    /// each validated in their own right.
    /// </summary>
    private static void ValidateRootShape(string root)
    {
        foreach (var owned in new[] { "bin", "installer" })
        {
            var path = Path.Combine(root, owned);
            if (Directory.Exists(path) || File.Exists(path))
            {
                ValidateDirectoryPath(path);
            }
        }
    }

    private static void ValidateInstallerShape(InstallationLayout layout)
    {
        ValidateExactNames(layout.InstallerDirectory, ["backups", "transaction.lock"]);
        ValidateDirectoryPath(layout.InstallerBackupsRoot);
        ValidateOptionalFile(Path.Combine(layout.InstallerDirectory, "transaction.lock"));
    }

    private static void ValidateExactNames(string directory, IEnumerable<string> allowed)
    {
        var allowedNames = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!allowedNames.Contains(Path.GetFileName(entry)))
            {
                throw Failure();
            }
        }
    }

    private static void ValidateDirectoryPath(string path)
    {
        if (!Directory.Exists(path))
        {
            throw Failure();
        }

        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Failure();
        }
    }

    private static void ValidateOptionalFile(string path)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
            {
                throw Failure();
            }

            return;
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Failure();
        }
        ValidateFileAcl(path);
    }

    private static void ValidateRequiredFile(string path)
    {
        if (!File.Exists(path))
        {
            throw Failure();
        }

        ValidateOptionalFile(path);
    }

    private static void ValidateFileHandle(FileStream stream, string expectedPath)
    {
        var identity = WindowsStagingRootLease.GetIdentity(stream.SafeFileHandle);
        if (identity.Attributes.HasFlag(FileAttributes.Directory) ||
            identity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !string.Equals(
                WindowsStagingRootLease.GetFinalPath(stream.SafeFileHandle),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure();
        }
    }

    private static bool IsBelow(string path, string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) +
                     Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Names the check that refused the layout.
    ///
    /// Every rejection used to produce the same sentence, so a refused installation reported
    /// only that something, somewhere, was wrong — useless to the user and to whoever has to
    /// diagnose it. The caller name costs nothing and turns the message into a lead.
    /// </summary>
    private static LocalAiPackageInstallationException Failure(
        [System.Runtime.CompilerServices.CallerMemberName] string? check = null) =>
        new($"The LocalAi installation layout is unsafe (check: {check ?? "unknown"}).");

    /// <summary>
    /// The same refusal, carrying which of the ACL conditions failed.
    ///
    /// <see cref="ValidateAcl"/> tests eight separate things and reported one sentence for all of
    /// them. That is workable while the only machine involved is the one that wrote the layout;
    /// it stops being workable the moment a different environment refuses it, because the message
    /// names neither the condition nor the principal, and there is nothing to act on. Deliberately
    /// a distinct name rather than an overload: <c>Failure("...")</c> would bind the reason to the
    /// caller-name parameter and quietly lose the check it came from.
    /// </summary>
    private static LocalAiPackageInstallationException AclFailure(
        string reason,
        [System.Runtime.CompilerServices.CallerMemberName] string? check = null) =>
        new($"The LocalAi installation layout is unsafe (check: {check ?? "unknown"}): {reason}.");

    /// <summary>
    /// The refusal a directory open produces, naming the directory and the NTSTATUS.
    ///
    /// Without those two facts the message was a dead end: three consecutive installations
    /// reported <c>check: OpenRelative</c> and nothing else, and the actual cause — one
    /// directory, held by a running process — could only be found by reproducing the native
    /// call by hand. The status is what separates "not there" from "held by someone else"
    /// from "no access", and the path says which of the eight directories it was.
    /// </summary>
    private static LocalAiPackageInstallationException OpenFailure(
        string path,
        int status,
        string check) =>
        new($"The LocalAi installation layout is unsafe (check: {check}): " +
            $"{path} could not be opened (NTSTATUS 0x{status:X8}).");

    private static void DisposeDirectories(
        IEnumerable<DirectoryEvidence> evidence,
        bool removeCreatedScaffold)
    {
        foreach (var directory in evidence.Reverse())
        {
            try
            {
                if (removeCreatedScaffold && directory.Created &&
                    WindowsStagingRootLease.GetIdentity(directory.Handle) == directory.Identity &&
                    string.Equals(
                        WindowsStagingRootLease.GetFinalPath(directory.Handle),
                        directory.CanonicalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        NativeDirectory.DeleteEmpty(directory.Handle);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or Win32Exception)
                    {
                    }
                }
            }
            finally
            {
                directory.Handle.Dispose();
            }
        }
    }

    private static void ValidateDirectoryAcl(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        ValidateAcl(security, directory: true);
    }

    private static void ValidateFileAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        ValidateAcl(security, directory: false);
    }

    private static void ValidateAcl(FileSystemSecurity security, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw AclFailure("the process has no user SID");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            user.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        };

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
        {
            throw AclFailure("the owner could not be read");
        }

        // Any principal the access rules would accept is acceptable as owner.
        //
        // This demanded the current user, and Windows does not always agree: an object created
        // by an elevated member of Administrators is owned by BUILTIN\Administrators, not by the
        // person who created it. An installation performed with elevation therefore failed its
        // own validation, which is how this surfaced — every installer test on an elevated CI
        // runner refused a layout the product had just written itself.
        //
        // It widens nothing. These three principals are exactly the ones already permitted to
        // hold FullControl below, so the set of identities that can change this object is the
        // same before and after. What still has to hold is that the current user is among them,
        // and that is checked after the rules.
        if (!allowed.Contains(owner.Value))
        {
            throw AclFailure("the owner is not the user, SYSTEM or Administrators");
        }

        if (directory && !security.AreAccessRulesProtected)
        {
            throw AclFailure("the directory still inherits access rules");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            var expectedInheritance = directory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;
            if (directory && rule.IsInherited)
            {
                throw AclFailure("an inherited rule survives on a protected directory");
            }

            if (rule.AccessControlType != AccessControlType.Allow)
            {
                throw AclFailure("a non-allow rule is present");
            }

            if (rule.IdentityReference is not SecurityIdentifier sid ||
                !allowed.Contains(sid.Value))
            {
                throw Failure(
                    $"an unexpected principal holds rights: {rule.IdentityReference?.Value}");
            }

            if ((rule.FileSystemRights & FileSystemRights.FullControl) !=
                FileSystemRights.FullControl)
            {
                throw AclFailure($"{sid.Value} holds less than FullControl");
            }

            if (rule.InheritanceFlags != expectedInheritance ||
                rule.PropagationFlags != PropagationFlags.None)
            {
                throw Failure(
                    $"{sid.Value} has inheritance {rule.InheritanceFlags}/" +
                    $"{rule.PropagationFlags}, expected {expectedInheritance}/None");
            }

            seen.Add(sid.Value);
        }

        // A subset of the allowed principals, not an exact match.
        //
        // Every rule above is already required to name an allowed principal, so nothing
        // unexpected can hold rights here — that is the property worth protecting. Demanding
        // all three additionally rejected ACLs that are stricter than required, and the
        // broker runtime writes exactly such an ACL on the shared root: the user and
        // Administrators, without SYSTEM. Insisting on equality refused installation on
        // every machine the runtime had ever touched, for a directory that was in no way
        // less safe.
        if (!seen.Contains(user.Value))
        {
            throw AclFailure("the current user holds no rights of its own");
        }
    }

    private sealed record DirectoryEvidence(
        SafeFileHandle Handle,
        WindowsStagingRootLease.FileIdentity Identity,
        string CanonicalPath,
        bool ValidateAcl,
        bool Created);

    public sealed class VersionTemporary : IDisposable
    {
        private readonly InstallationLayoutLease owner;
        private readonly WindowsStagingRootLease.FileIdentity identity;
        private bool published;
        private bool disposed;

        internal VersionTemporary(
            InstallationLayoutLease owner,
            SafeFileHandle handle,
            string canonicalPath)
        {
            this.owner = owner;
            Handle = handle;
            CanonicalPath = Path.GetFullPath(canonicalPath);
            identity = WindowsStagingRootLease.GetIdentity(handle);
            Revalidate();
        }

        public string CanonicalPath { get; private set; }
        internal SafeFileHandle Handle { get; }

        public void PublishAbsent(string version)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (published)
            {
                throw Failure();
            }

            owner.Publish(this, version);
        }

        internal void MarkPublished(string canonicalPath)
        {
            CanonicalPath = Path.GetFullPath(canonicalPath);
            published = true;
        }

        internal void Revalidate()
        {
            if (WindowsStagingRootLease.GetIdentity(Handle) != identity ||
                !string.Equals(
                    WindowsStagingRootLease.GetFinalPath(Handle),
                    CanonicalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Failure();
            }
            ValidateDirectoryAcl(CanonicalPath);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            try
            {
                if (!published)
                {
                    owner.Cleanup(this);
                }
            }
            finally
            {
                disposed = true;
                if (!published)
                {
                    Handle.Dispose();
                }

                owner.Unregister(this);
            }
        }
    }

    public sealed class TrustedLauncher : IDisposable
    {
        private readonly InstallationLayoutLease owner;
        private readonly FileStream stream;
        private readonly WindowsStagingRootLease.FileIdentity identity;
        private readonly VerifiedPackageFile expected;
        private bool disposed;

        internal TrustedLauncher(
            InstallationLayoutLease owner,
            FileStream stream,
            string canonicalPath,
            VerifiedPackageFile expected)
        {
            this.owner = owner;
            this.stream = stream;
            this.expected = expected;
            CanonicalPath = Path.GetFullPath(canonicalPath);
            identity = WindowsStagingRootLease.GetIdentity(stream.SafeFileHandle);
        }

        public string CanonicalPath { get; }

        public void Revalidate()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Revalidate();
            var actualIdentity = WindowsStagingRootLease.GetIdentity(stream.SafeFileHandle);
            if (actualIdentity != identity ||
                actualIdentity.Attributes.HasFlag(FileAttributes.Directory) ||
                actualIdentity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !string.Equals(
                    WindowsStagingRootLease.GetFinalPath(stream.SafeFileHandle),
                    CanonicalPath,
                    StringComparison.OrdinalIgnoreCase) ||
                stream.Length != expected.Length)
            {
                throw Failure();
            }

            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, expected.Sha256, StringComparison.Ordinal))
            {
                throw Failure();
            }
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                stream.Dispose();
            }
        }
    }

    public sealed class RetainedLauncherBackup : IDisposable
    {
        private const uint DuplicateSameAccess = 0x00000002;
        private readonly InstallationLayoutLease owner;
        private readonly FileStream stream;
        private readonly WindowsStagingRootLease.FileIdentity identity;
        private bool disposed;

        internal RetainedLauncherBackup(
            InstallationLayoutLease owner,
            FileStream stream,
            string canonicalPath,
            LauncherBackupMetadata metadata)
        {
            this.owner = owner;
            this.stream = stream;
            CanonicalPath = Path.GetFullPath(canonicalPath);
            Metadata = metadata;
            identity = WindowsStagingRootLease.GetIdentity(stream.SafeFileHandle);
        }

        public string CanonicalPath { get; }
        public LauncherBackupMetadata Metadata { get; }

        public void Revalidate()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Revalidate();
            ValidateFileHandle(stream, CanonicalPath);
            var actualIdentity = WindowsStagingRootLease.GetIdentity(stream.SafeFileHandle);
            if (actualIdentity != identity ||
                !string.Equals(CanonicalPath, Path.GetFullPath(Metadata.Path), StringComparison.OrdinalIgnoreCase) ||
                stream.Length != Metadata.Length)
            {
                throw Failure();
            }

            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            stream.Position = 0;
            if (!string.Equals(hash, Metadata.Sha256, StringComparison.Ordinal))
            {
                throw Failure();
            }
        }

        public FileStream OpenReadDuplicate()
        {
            Revalidate();
            if (!DuplicateHandle(
                    GetCurrentProcess(),
                    stream.SafeFileHandle,
                    GetCurrentProcess(),
                    out var duplicate,
                    0,
                    false,
                    DuplicateSameAccess))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new FileStream(duplicate, FileAccess.Read, 64 * 1024, isAsync: false);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                stream.Dispose();
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(
            IntPtr sourceProcessHandle,
            SafeFileHandle sourceHandle,
            IntPtr targetProcessHandle,
            out SafeFileHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);
    }

    public sealed class RetainedInstalledVersion : IDisposable
    {
        private readonly InstallationLayoutLease owner;
        private readonly IReadOnlyList<FileEvidence> files;
        private bool disposed;

        internal RetainedInstalledVersion(
            InstallationLayoutLease owner,
            string canonicalPath,
            IReadOnlyList<FileEvidence> files)
        {
            this.owner = owner;
            this.files = files;
            CanonicalPath = Path.GetFullPath(canonicalPath);
        }

        public string CanonicalPath { get; }

        public void Revalidate()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Revalidate();
            ValidateExactVersionDirectory(
                CanonicalPath,
                LocalAiPackageLayout.VersionRequiredFiles);
            foreach (var file in files)
            {
                ValidateFileHandle(file.Stream, file.CanonicalPath);
                var actualIdentity = WindowsStagingRootLease.GetIdentity(file.Stream.SafeFileHandle);
                if (actualIdentity != file.Identity || file.Stream.Length != file.Expected.Length)
                {
                    throw Failure();
                }

                file.Stream.Position = 0;
                var hash = Convert.ToHexString(SHA256.HashData(file.Stream));
                file.Stream.Position = 0;
                if (!string.Equals(hash, file.Expected.Sha256, StringComparison.Ordinal))
                {
                    throw Failure();
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var file in files)
            {
                file.Stream.Dispose();
            }
        }

        internal sealed record FileEvidence(
            FileStream Stream,
            string CanonicalPath,
            WindowsStagingRootLease.FileIdentity Identity,
            VerifiedPackageFile Expected);
    }

    private static class NativeDirectory
    {
        private const uint FileListDirectory = 0x00000001;
        private const uint FileReadAttributes = 0x00000080;
        private const uint Delete = 0x00010000;
        private const uint ReadControl = 0x00020000;
        private const uint Synchronize = 0x00100000;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileOpen = 1;
        private const uint FileCreate = 2;
        private const uint OpenAccess =
            FileListDirectory | FileReadAttributes | ReadControl | Synchronize;
        private const uint CreateAccess = OpenAccess | Delete;
        private const int OpenOrCreateAttempts = 3;
        private const int StatusUnsuccessful = unchecked((int)0xC0000001);
        private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
        private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
        private const uint FileDirectoryFile = 0x00000001;
        private const uint FileSynchronousIoNonalert = 0x00000020;
        private const uint FileOpenReparsePoint = 0x00200000;
        private const uint ObjCaseInsensitive = 0x00000040;
        private const uint OpenExistingDisposition = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        public static SafeFileHandle OpenAbsolute(string path)
        {
            var handle = CreateFileW(
                path,
                FileListDirectory | FileReadAttributes | ReadControl | Synchronize,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                OpenExistingDisposition,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return handle;
        }

        /// <summary>
        /// Opens a directory of the layout, creating it only when it is absent.
        ///
        /// The two cases deliberately ask for different rights. A directory this call creates
        /// is scaffold: if the installation fails before it commits, the lease deletes it
        /// again, and deleting by handle needs <c>DELETE</c>. A directory that was already
        /// there is never deleted by the installer, so asking for <c>DELETE</c> on it buys
        /// nothing — and costs everything, because Windows refuses <c>DELETE</c> on a
        /// directory another process holds without <c>FILE_SHARE_DELETE</c>. The runtime root
        /// is exactly such a directory whenever the broker is running: it is that process's
        /// working directory. One <c>FILE_OPEN_IF</c> for both cases therefore refused every
        /// upgrade on a machine where LocalAi was in use, at the first gate of the install,
        /// before the step that stops those processes could ever run.
        ///
        /// The retry covers the gap between the two calls: another installer or the runtime
        /// itself may create the directory after the open failed, or remove it after the
        /// create collided. Both resolve by trying the other operation once more.
        /// </summary>
        public static OpenedDirectory OpenOrCreate(
            SafeFileHandle parent,
            string name,
            string expectedPath)
        {
            for (var attempt = 0; ; attempt++)
            {
                var opened = TryOpenRelative(parent, name, expectedPath, FileOpen, OpenAccess);
                if (opened.Directory is { } existing)
                {
                    return existing;
                }

                if (!IsMissing(opened.Status) || attempt >= OpenOrCreateAttempts)
                {
                    throw OpenFailure(expectedPath, opened.Status, nameof(OpenOrCreate));
                }

                var created = TryOpenRelative(parent, name, expectedPath, FileCreate, CreateAccess);
                if (created.Directory is { } fresh)
                {
                    return fresh;
                }

                if (created.Status != StatusObjectNameCollision || attempt >= OpenOrCreateAttempts)
                {
                    throw OpenFailure(expectedPath, created.Status, nameof(OpenOrCreate));
                }
            }
        }

        public static SafeFileHandle OpenExisting(
            SafeFileHandle parent,
            string name,
            string expectedPath)
        {
            var opened = TryOpenRelative(parent, name, expectedPath, FileOpen, OpenAccess);
            return opened.Directory?.Handle
                ?? throw OpenFailure(expectedPath, opened.Status, nameof(OpenExisting));
        }

        public static SafeFileHandle CreateExclusive(
            SafeFileHandle parent,
            string name,
            string expectedPath)
        {
            var created = TryOpenRelative(parent, name, expectedPath, FileCreate, CreateAccess);
            return created.Directory?.Handle
                ?? throw OpenFailure(expectedPath, created.Status, nameof(CreateExclusive));
        }

        private static bool IsMissing(int status) =>
            status is StatusObjectNameNotFound or StatusObjectPathNotFound;

        private static OpenAttempt TryOpenRelative(
            SafeFileHandle parent,
            string name,
            string expectedPath,
            uint disposition,
            uint desiredAccess)
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(['\\', '/', ':']) >= 0)
            {
                throw Failure();
            }

            var nameBuffer = IntPtr.Zero;
            var namePointer = IntPtr.Zero;
            var descriptorPointer = IntPtr.Zero;
            var nativeHandle = IntPtr.Zero;
            var parentRef = false;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(name);
                var unicode = new UnicodeString
                {
                    Length = checked((ushort)(name.Length * sizeof(char))),
                    MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                    Buffer = nameBuffer,
                };
                namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                Marshal.StructureToPtr(unicode, namePointer, false);
                var descriptor = SecurityDescriptor();
                descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
                Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
                parent.DangerousAddRef(ref parentRef);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = namePointer,
                    Attributes = ObjCaseInsensitive,
                    SecurityDescriptor = descriptorPointer,
                };
                var status = NtCreateFile(
                    out nativeHandle,
                    desiredAccess,
                    ref attributes,
                    out var io,
                    IntPtr.Zero,
                    FileAttributeNormal,
                    FileShareRead | FileShareWrite,
                    disposition,
                    FileDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint,
                    IntPtr.Zero,
                    0);
                if (status < 0 || nativeHandle is 0 or -1)
                {
                    return new(status is 0 ? StatusUnsuccessful : status, null);
                }

                var handle = new SafeFileHandle(nativeHandle, ownsHandle: true);
                nativeHandle = IntPtr.Zero;
                WindowsStagingRootLease.ValidateDirectoryHandle(handle, expectedPath);
                return new(0, new(handle, io.Information.ToUInt64() == 2));
            }
            finally
            {
                if (nativeHandle is not 0 and not -1)
                {
                    new SafeFileHandle(nativeHandle, ownsHandle: true).Dispose();
                }

                if (parentRef)
                {
                    parent.DangerousRelease();
                }

                if (descriptorPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(descriptorPointer);
                }

                if (namePointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(namePointer);
                }

                if (nameBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nameBuffer);
                }
            }
        }

        public static void Rename(
            SafeFileHandle handle,
            SafeFileHandle targetParent,
            string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName) || targetName is "." or ".." ||
                targetName.IndexOfAny(['\\', '/', ':']) >= 0)
            {
                throw Failure();
            }

            var bytes = System.Text.Encoding.Unicode.GetBytes(targetName);
            var rootOffset = IntPtr.Size == 8 ? 8 : 4;
            var lengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = lengthOffset + sizeof(uint);
            var buffer = Marshal.AllocHGlobal(nameOffset + bytes.Length);
            var parentRef = false;
            try
            {
                for (var index = 0; index < nameOffset; index++)
                {
                    Marshal.WriteByte(buffer, index, 0);
                }

                targetParent.DangerousAddRef(ref parentRef);
                Marshal.WriteIntPtr(buffer, rootOffset, targetParent.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
                Marshal.Copy(bytes, 0, buffer + nameOffset, bytes.Length);
                var status = NtSetInformationFile(
                    handle,
                    out _,
                    buffer,
                    (uint)(nameOffset + bytes.Length),
                    10);
                if (status < 0)
                {
                    throw Failure();
                }
            }
            finally
            {
                if (parentRef)
                {
                    targetParent.DangerousRelease();
                }

                Marshal.FreeHGlobal(buffer);
            }
        }

        public static void DeleteEmpty(SafeFileHandle handle)
        {
            var disposition = new FileDispositionInformation { DeleteFile = true };
            if (!SetFileInformationByHandle(
                    handle,
                    4,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public static void DeleteFile(string path)
        {
            using var handle = CreateFileW(
                path,
                FileReadAttributes | Delete | ReadControl | Synchronize,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                OpenExistingDisposition,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var identity = WindowsStagingRootLease.GetIdentity(handle);
            if (identity.Attributes.HasFlag(FileAttributes.Directory) ||
                identity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !string.Equals(
                    WindowsStagingRootLease.GetFinalPath(handle),
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Failure();
            }

            ValidateFileAcl(path);
            var disposition = new FileDispositionInformation { DeleteFile = true };
            if (!SetFileInformationByHandle(
                    handle,
                    4,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static byte[] SecurityDescriptor()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw Failure();
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inheritance =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security.GetSecurityDescriptorBinaryForm();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(
            out IntPtr fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            IntPtr allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle fileHandle,
            int fileInformationClass,
            ref FileDispositionInformation fileInformation,
            uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public UIntPtr Information;
        }

        internal sealed record OpenedDirectory(SafeFileHandle Handle, bool Created);

        /// <summary>
        /// One attempt at opening a directory: the handle, or the NTSTATUS that refused it.
        /// The status is carried rather than thrown so the caller can tell "it is not there"
        /// from "someone else holds it", which are opposite situations with opposite answers.
        /// </summary>
        internal sealed record OpenAttempt(int Status, OpenedDirectory? Directory);
    }
}

internal delegate void VersionDirectoryPublisher(
    SafeFileHandle source,
    SafeFileHandle targetParent,
    string targetName);
