using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LocalAi.Installer.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class LocalAiPackageInstallerTests : IDisposable
{
    private readonly string localAppData = Path.Combine(
        Path.GetTempPath(),
        "LocalAiPackageInstallerTests",
        Guid.NewGuid().ToString("N"));

    public LocalAiPackageInstallerTests()
    {
        Directory.CreateDirectory(localAppData);
    }

    [Fact]
    public void Package_layout_keeps_stable_launcher_outside_version_files()
    {
        Assert.Equal("localai-launcher.exe", LocalAiPackageLayout.StableLauncherFile);
        Assert.DoesNotContain(
            LocalAiPackageLayout.StableLauncherFile,
            LocalAiPackageLayout.VersionRequiredFiles);
        Assert.Contains(
            LocalAiPackageLayout.StableLauncherFile,
            LocalAiPackageLayout.PackageArtifactFiles);
    }

    [Fact]
    public async Task Fresh_install_publishes_version_and_launcher_then_activates_exact_path()
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            AssertInstalledVersionFilesLocked(layout, "v1");
            WritePointer("v1");
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });
        var installer = new LocalAiPackageInstaller(
            runner,
            new ExistingLocalAiInspector(new SystemFileSystemProbe()),
            TimeSpan.FromSeconds(5));

        var result = await installer.InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
        Assert.Equal("v1", result.Version);
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            Assert.Equal(
                Content(file),
                File.ReadAllBytes(Path.Combine(layout.VersionsRoot, "v1", file)));
        }

        Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
        var call = Assert.Single(runner.Calls);
        Assert.Equal(layout.LauncherPath, call.Executable);
        Assert.Equal(
            ["activate", "v1", "--stop-running", "--if-current-missing"],
            call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
    }

    [Fact]
    public async Task Concurrent_installers_serialize_before_reinspection_and_launcher_handoff()
    {
        using var packageA = Package("v1");
        using var packageB = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstInspector = new RecordingInspector(
            new ExistingLocalAiInspector(new SystemFileSystemProbe()),
            () => events.Enqueue("A-inspect"));
        var secondInspector = new RecordingInspector(
            new ExistingLocalAiInspector(new SystemFileSystemProbe()),
            () => events.Enqueue("B-inspect"));
        var firstRunner = new RecordingRunner(async (_, arguments, _, _) =>
        {
            events.Enqueue("A-start");
            firstStarted.SetResult();
            await releaseFirst.Task;
            WritePointer(arguments[1]);
            events.Enqueue("A-end");
            return new ProcessResult(0, "", "", false, false);
        });
        var secondRunner = new RecordingRunner((_, arguments, _, _) =>
        {
            events.Enqueue("B-start");
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });
        var first = new LocalAiPackageInstaller(firstRunner, firstInspector, TimeSpan.FromSeconds(5));
        var second = new LocalAiPackageInstaller(secondRunner, secondInspector, TimeSpan.FromSeconds(5));

        var firstTask = first.InstallAsync(packageA, layout, TestContext.Current.CancellationToken);
        await firstStarted.Task;
        var secondTask = second.InstallAsync(packageB, layout, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(0, secondInspector.CallCount);
        Assert.False(secondTask.IsCompleted);
        releaseFirst.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, results[0].Status);
        Assert.Equal(LocalAiPackageInstallStatus.AlreadyInstalled, results[1].Status);
        Assert.Equal(1, secondInspector.CallCount);
        Assert.Equal(
            ["A-inspect", "A-start", "A-end", "B-inspect", "B-start"],
            events.ToArray());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Installer_transaction_timeout_returns_sanitized_busy_without_inspection()
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        using var held = InstallerTransactionLease.Acquire(
            layout,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        var inspector = new RecordingInspector(
            new ExistingLocalAiInspector(new SystemFileSystemProbe()));
        var installer = new LocalAiPackageInstaller(
            new RecordingRunner((_, _, _, _) => throw new InvalidOperationException()),
            inspector,
            TimeSpan.FromMilliseconds(50));

        var result = await installer.InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Busy, result.Status);
        Assert.Equal("Another LocalAi installation is already in progress.", result.Reason);
        Assert.Equal(0, inspector.CallCount);
        Assert.False(Directory.Exists(layout.Root));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Installer_transaction_rejects_hostile_mutex_with_sanitized_failure()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var factory = new ThrowingSecureMutexFactory(
            new UnauthorizedAccessException("hostile mutex descriptor"));

        var error = Assert.Throws<InstallerTransactionBusyException>(() =>
            InstallerTransactionLease.Acquire(
                layout,
                TimeSpan.Zero,
                CancellationToken.None,
                factory));

        Assert.Equal("Another LocalAi installation is already in progress.", error.Message);
        Assert.DoesNotContain("hostile", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fresh_inspection_does_not_adopt_foreign_valid_bin_tree()
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var inspector = new DelegateInspector(_ =>
        {
            Directory.CreateDirectory(layout.VersionsRoot);
            Directory.CreateDirectory(layout.LauncherDirectory);
            var runtime = Path.Combine(layout.Root, "runtime");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "keep.txt"), "keep");
            return new ExistingLocalAiSnapshot(ExistingLocalAiState.Absent, null, null, null);
        });
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        var result = await new LocalAiPackageInstaller(runner, inspector, TimeSpan.FromSeconds(5))
            .InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Refused, result.Status);
        Assert.Empty(runner.Calls);
        Assert.True(Directory.Exists(layout.VersionsRoot));
        Assert.True(Directory.Exists(layout.LauncherDirectory));
        Assert.False(Directory.Exists(layout.InstallerDirectory));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(layout.Root, "runtime", "keep.txt")));
        Assert.Equal(
            ["launcher", "versions"],
            Directory.EnumerateFileSystemEntries(layout.BinRoot)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.VersionsRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.LauncherDirectory));
    }

    /// <summary>
    /// The case that made the wizard unusable on any machine that actually ran LocalAi.
    ///
    /// Every tool started through the launcher — each MCP server of a connected client — holds
    /// a shared lease on current.lock for its whole lifetime. Reading the pointer exclusively
    /// meant the installation was refused before it reached the activation that passes
    /// --stop-running, and refused with a message blaming the pointer, which was intact.
    /// </summary>
    [Fact]
    public async Task A_client_holding_the_shared_lease_does_not_block_an_upgrade()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        using var connectedClient = ActivationCoordinator.AcquireShared(
            layout.BinRoot,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
        Assert.Equal("v2", result.Version);
        // Stopping those very processes is the activation's job, and it is finally reached.
        Assert.Contains("--stop-running", Assert.Single(runner.Calls).Arguments);
    }

    /// <summary>
    /// Windows refuses to overwrite a running executable, and every connected client keeps a
    /// launcher process alive per tool it uses, so the replacement failed on exactly the
    /// machines that were using LocalAi. Activation stops those processes — one step past the
    /// failure — so the new launcher is asked to stop them before it takes the old one's place.
    /// </summary>
    [Fact]
    public async Task A_launcher_held_by_a_running_tool_is_stopped_and_then_replaced()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        // How Windows holds a running executable: readable, and refusing to be replaced.
        var runningTool = new FileStream(
            layout.LauncherPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        string? stoppedBy = null;
        byte[]? launcherWhenStopped = null;
        var runner = new RecordingRunner((executable, arguments, _, _) =>
        {
            if (arguments is ["stop"])
            {
                stoppedBy = executable;
                launcherWhenStopped = File.ReadAllBytes(layout.LauncherPath);
                runningTool.Dispose();
            }
            else
            {
                WritePointer(arguments[1]);
            }

            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        try
        {
            var result = await Installer(runner).InstallAsync(
                package,
                layout,
                TestContext.Current.CancellationToken);

            Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
            Assert.Equal(["stop"], runner.Calls[0].Arguments);
            // Asked of the new launcher, not the installed one: a launcher old enough to lack
            // the verb is precisely the one that has to be replaced.
            Assert.NotEqual(layout.LauncherPath, stoppedBy);
            // And while the old one was still in place, which is the whole point of the order.
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes("prior-launcher"), launcherWhenStopped);
            Assert.Contains("activate", runner.Calls[1].Arguments);
            Assert.Equal(
                Content(LocalAiPackageLayout.StableLauncherFile),
                File.ReadAllBytes(layout.LauncherPath));
        }
        finally
        {
            runningTool.Dispose();
        }
    }

    [Fact]
    public async Task Nothing_is_stopped_when_the_launcher_can_simply_be_replaced()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
        // Stopping a client's tools is disruptive; nothing was in the way, so nothing was
        // touched, and the activation that follows still carries --stop-running for itself.
        Assert.DoesNotContain(runner.Calls, call => call.Arguments is ["stop"]);
    }

    [Fact]
    public async Task A_contended_lock_is_reported_as_busy_rather_than_as_a_broken_pointer()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        // Another activation in flight — the one thing a reader genuinely has to wait for.
        // Held as the coordinator's exclusive lease holds it, by the file share mode alone: an
        // ActivationExclusiveLease also owns a named mutex, and a Win32 mutex has to be
        // released by the thread that took it, which an await in this test cannot promise.
        using var otherActivation = new FileStream(
            Path.Combine(layout.BinRoot, "current.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var runner = new RecordingRunner((_, _, _, _) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false)));

        var result = await new LocalAiPackageInstaller(
                runner,
                new ExistingLocalAiInspector(new SystemFileSystemProbe()),
                TimeSpan.FromMilliseconds(250))
            .InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Busy, result.Status);
        Assert.Contains("activating", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("pointer", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Compatible_upgrade_backs_up_launcher_and_activates_new_version()
    {
        var priorLauncher = System.Text.Encoding.UTF8.GetBytes("prior-launcher");
        CreateExisting("v1", priorLauncher);
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        Exception? runnerFailure = null;
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            try
            {
                var backupPath = Assert.Single(
                    Directory.EnumerateFiles(
                        layout.InstallerBackupsRoot,
                        LocalAiPackageLayout.StableLauncherFile,
                        SearchOption.AllDirectories));
                Assert.True(Record.Exception(() => File.WriteAllText(backupPath, "tampered"))
                    is IOException or UnauthorizedAccessException);
                Assert.True(Record.Exception(() => File.Delete(backupPath))
                    is IOException or UnauthorizedAccessException);
                var replacement = Path.Combine(Path.GetDirectoryName(backupPath)!, "replacement.exe");
                File.WriteAllText(replacement, "replacement");
                Assert.True(Record.Exception(() => File.Move(replacement, backupPath, overwrite: true))
                    is IOException or UnauthorizedAccessException);
                File.Delete(replacement);
                WritePointer(arguments[1]);
                return Task.FromResult(new ProcessResult(0, "", "", false, false));
            }
            catch (Exception exception)
            {
                runnerFailure = exception;
                throw;
            }
        });
        var priorPointer = File.ReadAllBytes(layout.CurrentPointerPath);
        var installer = Installer(runner);

        var result = await installer.InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Null(runnerFailure);
        Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
        Assert.Equal("v1", result.PriorVersion);
        Assert.NotNull(result.LauncherBackupPath);
        Assert.Equal(priorLauncher, File.ReadAllBytes(result.LauncherBackupPath));
        Assert.Equal(priorLauncher.LongLength, result.LauncherBackup!.Length);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(priorLauncher)),
            result.LauncherBackup.Sha256);
        Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
        var call = Assert.Single(runner.Calls);
        Assert.Equal(
            [
                "activate", "v2", "--stop-running", "--if-current-sha256",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(priorPointer)),
            ],
            call.Arguments);
        Assert.True(Directory.Exists(Path.Combine(layout.VersionsRoot, "v1")));
        Assert.True(Directory.Exists(Path.Combine(layout.VersionsRoot, "v2")));
    }

    [Fact]
    public async Task Unrecognized_fresh_inspection_refuses_without_mutation()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        Directory.CreateDirectory(layout.Root);
        File.WriteAllText(Path.Combine(layout.Root, "unexpected.txt"), "keep");
        using var package = Package("v2");
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());
        var installer = Installer(runner);

        var result = await installer.InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Refused, result.Status);
        Assert.Empty(runner.Calls);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(layout.Root, "unexpected.txt")));
    }

    [Fact]
    public async Task Compatible_pointer_with_unexpected_structure_is_refused_without_mutation()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var unexpected = Path.Combine(layout.BinRoot, "unexpected.txt");
        File.WriteAllText(unexpected, "keep");
        using var package = Package("v2");
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Refused, result.Status);
        Assert.Empty(runner.Calls);
        Assert.Equal("keep", File.ReadAllText(unexpected));
        Assert.False(Directory.Exists(Path.Combine(layout.VersionsRoot, "v2")));
    }

    [Fact]
    public async Task Runtime_siblings_under_LocalAi_root_are_preserved()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runtime = Path.Combine(layout.Root, "runtime");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "broker.lock"), "keep");
        using var package = Package("v2");
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(runtime, "broker.lock")));
    }

    [Theory]
    [InlineData("unexpected.bin")]
    [InlineData("CON")]
    public async Task File_shaped_entries_under_versions_are_refused(string name)
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var unexpected = Path.Combine(layout.VersionsRoot, name);
        File.WriteAllText(unexpected, "keep");
        using var package = Package("v2");
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Refused, result.Status);
        Assert.Empty(runner.Calls);
        Assert.Equal("keep", File.ReadAllText(unexpected));
    }

    [Fact]
    public async Task Existing_version_with_extra_entry_is_refused_without_mutation()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var extra = Path.Combine(layout.VersionsRoot, "v1", "unexpected.txt");
        File.WriteAllText(extra, "keep");
        using var package = Package("v2");
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Refused, result.Status);
        Assert.Empty(runner.Calls);
        Assert.Equal("keep", File.ReadAllText(extra));
    }

    [Fact]
    public async Task Existing_version_with_packaged_launcher_can_be_upgraded()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var packagedLauncher = Path.Combine(
            layout.VersionsRoot,
            "v1",
            LocalAiPackageLayout.StableLauncherFile);
        File.WriteAllBytes(packagedLauncher, System.Text.Encoding.UTF8.GetBytes("versioned-launcher"));
        using var package = Package("v2");
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            WritePointer("v2");
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
        Assert.Contains("\"version\":\"v2\"", File.ReadAllText(layout.CurrentPointerPath));
        Assert.True(File.Exists(packagedLauncher));
        Assert.Single(runner.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Layout_lease_atomically_creates_and_blocks_root_rename()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);

        using var lease = InstallationLayoutLease.Acquire(layout);

        Assert.True(Directory.Exists(layout.VersionsRoot));
        Assert.True(Directory.Exists(layout.LauncherDirectory));
        Assert.True(Directory.Exists(layout.InstallerBackupsRoot));
        Assert.ThrowsAny<IOException>(() =>
            Directory.Move(layout.Root, layout.Root + ".moved"));
        lease.Revalidate();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Layout_lease_refuses_file_collision_without_replacing_it()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        File.WriteAllText(layout.Root, "winner");

        var error = Record.Exception(() =>
        {
            using var unexpected = InstallationLayoutLease.Acquire(layout);
        });
        Assert.IsType<LocalAiPackageInstallationException>(error);

        Assert.Equal("winner", File.ReadAllText(layout.Root));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Layout_lease_refuses_unsafe_version_directory_without_deleting_it()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var unsafeVersion = Path.Combine(layout.VersionsRoot, "CON");
        Directory.CreateDirectory(unsafeVersion);

        Assert.Throws<LocalAiPackageInstallationException>(() =>
            InstallationLayoutLease.Acquire(layout));

        Assert.True(Directory.Exists(unsafeVersion));
    }

    [Theory]
    [InlineData("permissive-parent")]
    [InlineData("delete-child")]
    [InlineData("inherited-ace")]
    [SupportedOSPlatform("windows")]
    public void Layout_lease_rejects_unsafe_existing_acl_without_mutation(string failure)
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        using (var lease = InstallationLayoutLease.Acquire(layout))
        {
            lease.CommitScaffold();
        }

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        if (failure == "inherited-ace")
        {
            var rootSecurity = new DirectoryInfo(layout.Root).GetAccessControl();
            rootSecurity.AddAccessRule(new FileSystemAccessRule(
                everyone,
                FileSystemRights.WriteData,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(layout.Root).SetAccessControl(rootSecurity);
            var binSecurity = new DirectoryInfo(layout.BinRoot).GetAccessControl();
            binSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new DirectoryInfo(layout.BinRoot).SetAccessControl(binSecurity);
        }
        else
        {
            var target = failure == "permissive-parent" ? layout.Root : layout.BinRoot;
            var security = new DirectoryInfo(target).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                everyone,
                failure == "delete-child"
                    ? FileSystemRights.DeleteSubdirectoriesAndFiles
                    : FileSystemRights.WriteData,
                AccessControlType.Allow));
            new DirectoryInfo(target).SetAccessControl(security);
        }

        var error = Record.Exception(() =>
        {
            using var unexpected = InstallationLayoutLease.Acquire(layout);
        });
        Assert.IsType<LocalAiPackageInstallationException>(error);
        Assert.True(Directory.Exists(layout.BinRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.VersionsRoot));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Layout_version_temporary_publishes_absent_and_retains_identity()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        using var lease = InstallationLayoutLease.Acquire(layout);
        using var temporary = lease.CreateVersionTemporary();
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            File.WriteAllBytes(Path.Combine(temporary.CanonicalPath, file), Content(file));
        }

        temporary.PublishAbsent("v1");

        var versionPath = Path.Combine(layout.VersionsRoot, "v1");
        Assert.Equal(versionPath, temporary.CanonicalPath);
        Assert.True(Directory.Exists(versionPath));
        Assert.ThrowsAny<IOException>(() =>
            Directory.Move(versionPath, versionPath + ".moved"));
        lease.Revalidate();
    }

    [Fact]
    public void Parent_layout_lease_cleans_forgotten_version_temporary()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var lease = InstallationLayoutLease.Acquire(layout);
        var temporary = lease.CreateVersionTemporary();
        var path = temporary.CanonicalPath;
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            File.WriteAllBytes(Path.Combine(path, file), Content(file));
        }

        lease.Dispose();

        Assert.False(Directory.Exists(path));
        temporary.Dispose();
    }

    [Fact]
    public void Temporary_cleanup_failure_closes_handle_and_repeated_dispose_is_safe()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var lease = InstallationLayoutLease.Acquire(layout);
        var temporary = lease.CreateVersionTemporary();
        var path = temporary.CanonicalPath;
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            File.WriteAllBytes(Path.Combine(path, file), Content(file));
        }

        var lockedPath = Path.Combine(path, LocalAiPackageLayout.VersionRequiredFiles[0]);
        var locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var error = Record.Exception(temporary.Dispose);
        lease.Dispose();
        locked.Dispose();

        Assert.IsType<IOException>(error);
        temporary.Dispose();
        var moved = path + ".moved";
        Directory.Move(path, moved);
        Assert.True(Directory.Exists(moved));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Retained_launcher_backup_rejects_wrong_handle_and_metadata()
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        using var lease = InstallationLayoutLease.Acquire(layout);
        File.WriteAllBytes(layout.LauncherPath, System.Text.Encoding.UTF8.GetBytes("prior-launcher"));

        using var backup = lease.CreateLauncherBackup();
        var wrongPath = Path.Combine(layout.LauncherDirectory, "wrong.exe");
        File.WriteAllText(wrongPath, "wrong");

        Assert.Throws<LocalAiPackageInstallationException>(() =>
            lease.RetainLauncherBackup(wrongPath, backup.Metadata));
        File.Delete(wrongPath);
        Assert.Throws<LocalAiPackageInstallationException>(() =>
            lease.RetainLauncherBackup(
                backup.CanonicalPath,
                backup.Metadata with { Length = backup.Metadata.Length + 1 }));
        backup.Revalidate();
    }

    [Fact]
    public async Task Exact_existing_version_is_idempotent_but_still_updates_launcher()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"), packageContent: true);
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            AssertInstalledVersionFilesLocked(layout, "v1");
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.AlreadyInstalled, result.Status);
        Assert.Single(runner.Calls);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(layout.VersionsRoot),
            path => Path.GetFileName(path).StartsWith(".install-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Version_allowlist_drift_during_launcher_handoff_never_reports_activation()
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var extra = Path.Combine(layout.VersionsRoot, "v1", "unexpected.txt");
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            File.WriteAllText(extra, "foreign");
            WritePointer("v1");
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.ManualRecoveryRequired, result.Status);
        Assert.Equal("foreign", File.ReadAllText(extra));
        Assert.DoesNotContain(
            result.Status,
            new[] { LocalAiPackageInstallStatus.Installed, LocalAiPackageInstallStatus.AlreadyInstalled });
    }

    [Fact]
    public async Task Verified_launcher_and_ancestors_are_locked_through_process_invocation()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((executable, arguments, _, _) =>
        {
            Assert.Equal(layout.LauncherPath, executable);
            Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(executable));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(executable, "replaced"));
            Assert.ThrowsAny<IOException>(() => File.Delete(executable));
            Assert.ThrowsAny<IOException>(() =>
                Directory.Move(layout.LauncherDirectory, layout.LauncherDirectory + ".moved"));
            Assert.ThrowsAny<IOException>(() =>
                Directory.Move(layout.BinRoot, layout.BinRoot + ".moved"));
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
    }

    [Fact]
    public async Task Cancellation_before_process_start_preserves_prior_installation()
    {
        var priorLauncher = System.Text.Encoding.UTF8.GetBytes("prior-launcher");
        CreateExisting("v1", priorLauncher);
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var priorPointer = File.ReadAllBytes(layout.CurrentPointerPath);
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Installer(runner).InstallAsync(package, layout, cancellation.Token));

        Assert.Empty(runner.Calls);
        Assert.Equal(priorLauncher, File.ReadAllBytes(layout.LauncherPath));
        Assert.Equal(priorPointer, File.ReadAllBytes(layout.CurrentPointerPath));
        Assert.False(Directory.Exists(Path.Combine(layout.VersionsRoot, "v2")));
    }

    [Fact]
    public async Task Existing_mismatched_target_is_an_immutable_conflict_without_writes()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var target = Path.Combine(layout.VersionsRoot, "v2");
        using (var lease = InstallationLayoutLease.Acquire(layout))
        using (var temporary = lease.CreateVersionTemporary())
        {
            foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
            {
                File.WriteAllBytes(Path.Combine(temporary.CanonicalPath, file), Content(file));
            }

            File.WriteAllText(
                Path.Combine(
                    temporary.CanonicalPath,
                    LocalAiPackageLayout.VersionRequiredFiles[0]),
                "mismatch");
            temporary.PublishAbsent("v2");
            lease.CommitScaffold();
        }

        var before = Directory.EnumerateFiles(target).ToDictionary(
            path => Path.GetFileName(path)!,
            File.ReadAllBytes);
        using var package = Package("v2");
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.ImmutableConflict, result.Status);
        Assert.Empty(runner.Calls);
        foreach (var file in before)
        {
            Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(target, file.Key!)));
        }
    }

    [Fact]
    public async Task Upgrade_failure_after_pointer_change_restores_launcher_and_rolls_back_through_it()
    {
        var priorLauncher = System.Text.Encoding.UTF8.GetBytes("prior-launcher");
        CreateExisting("v1", priorLauncher);
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var attempt = 0;
        var priorPointer = File.ReadAllBytes(layout.CurrentPointerPath);
        var expectedNewPointer = LocalAi.Contracts.Activation.CurrentPointerSnapshot.CreateCanonicalBytes("v2");
        var runner = new RecordingRunner((executable, arguments, _, _) =>
        {
            attempt++;
            Assert.Equal(layout.LauncherPath, executable);
            if (attempt == 1)
            {
                Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
                Assert.Equal(
                    [
                        "activate", "v2", "--stop-running", "--if-current-sha256",
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(priorPointer)),
                    ],
                    arguments);
                WritePointer("v2");
                return Task.FromResult(new ProcessResult(17, "", "", false, false));
            }

            Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
            Assert.Equal(
                [
                    "activate", "v1", "--stop-running", "--if-current-sha256",
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedNewPointer)),
                ],
                arguments);
            WritePointer("v1");
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.RolledBack, result.Status);
        Assert.Equal(priorLauncher, File.ReadAllBytes(layout.LauncherPath));
        Assert.Equal("v1", ReadPointerVersion(layout.CurrentPointerPath));
        Assert.True(Directory.Exists(Path.Combine(layout.VersionsRoot, "v2")));
        Assert.Equal(
            Path.Combine(layout.VersionsRoot, "v2"),
            result.InactivePublishedVersionPath);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Third_pointer_after_failed_activation_is_indeterminate_and_never_clobbered()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            WritePointer("v3");
            return Task.FromResult(new ProcessResult(17, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Indeterminate, result.Status);
        Assert.Equal("v3", ReadPointerVersion(layout.CurrentPointerPath));
        Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Same_version_raw_pointer_drift_after_failed_activation_is_indeterminate()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var drifted = System.Text.Encoding.UTF8.GetBytes(
            "{ \"schemaVersion\": 1, \"version\": \"v2\" }");
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            File.WriteAllBytes(layout.CurrentPointerPath, drifted);
            return Task.FromResult(new ProcessResult(17, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Indeterminate, result.Status);
        Assert.Equal(drifted, File.ReadAllBytes(layout.CurrentPointerPath));
        Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Rollback_requires_exact_prior_pointer_bytes_before_restoring_old_launcher()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var driftedPrior = System.Text.Encoding.UTF8.GetBytes(
            "{ \"schemaVersion\": 1, \"version\": \"v1\" }");
        var calls = 0;
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            calls++;
            if (calls == 1)
            {
                WritePointer("v2");
                return Task.FromResult(new ProcessResult(17, "", "", false, false));
            }

            File.WriteAllBytes(layout.CurrentPointerPath, driftedPrior);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Indeterminate, result.Status);
        Assert.Equal(driftedPrior, File.ReadAllBytes(layout.CurrentPointerPath));
        Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Oversized_pointer_after_failed_activation_is_indeterminate_without_clobbering()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var oversized = Enumerable.Repeat((byte)'X',
            LocalAi.Contracts.Activation.CurrentPointerSnapshot.MaximumBytes + 1).ToArray();
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            File.WriteAllBytes(layout.CurrentPointerPath, oversized);
            return Task.FromResult(new ProcessResult(17, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Indeterminate, result.Status);
        Assert.Equal(oversized, File.ReadAllBytes(layout.CurrentPointerPath));
        Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("cancellation")]
    [InlineData("termination")]
    public async Task Cancellation_or_termination_after_process_start_rolls_back_observed_pointer(
        string failure)
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var calls = 0;
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            calls++;
            if (calls == 1)
            {
                WritePointer("v2");
                if (failure == "cancellation")
                {
                    throw new OperationCanceledException();
                }

                throw new ProcessTerminationException(
                    42,
                    ProcessTerminationCause.Timeout,
                    "terminated");
            }

            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });
        var layout = InstallationLayout.FromLocalAppData(localAppData);

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.RolledBack, result.Status);
        Assert.Equal("v1", ReadPointerVersion(layout.CurrentPointerPath));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("win32")]
    [InlineData("unauthorized")]
    [InlineData("security")]
    public async Task Native_process_failures_are_classified_when_activation_and_rollback_fail(
        string failure)
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            if (arguments[1] == "v2")
            {
                WritePointer("v2");
            }

            throw failure switch
            {
                "io" => new IOException("native I/O failed"),
                "win32" => new Win32Exception(5, "native startup failed"),
                "unauthorized" => new UnauthorizedAccessException("native access failed"),
                "security" => new SecurityException("native policy failed"),
                _ => new InvalidOperationException(),
            };
        });
        var layout = InstallationLayout.FromLocalAppData(localAppData);

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.RollbackFailed, result.Status);
        Assert.Equal("v2", ReadPointerVersion(layout.CurrentPointerPath));
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(
            "Activation and rollback both failed; manual recovery is required.",
            result.Reason);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("termination")]
    [InlineData("io")]
    public async Task Rollback_commit_is_honored_even_when_rollback_process_reports_failure(
        string failure)
    {
        var priorLauncher = System.Text.Encoding.UTF8.GetBytes("prior-launcher");
        CreateExisting("v1", priorLauncher);
        using var package = Package("v2");
        var calls = 0;
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            calls++;
            WritePointer(arguments[1]);
            if (calls == 1)
            {
                return Task.FromResult(new ProcessResult(17, "", "", false, false));
            }

            return failure switch
            {
                "timeout" => Task.FromResult(new ProcessResult(null, "", "", true, false)),
                "termination" => throw new ProcessTerminationException(
                    42,
                    ProcessTerminationCause.Timeout,
                    "rollback terminated"),
                "io" => throw new IOException("rollback native failure"),
                _ => throw new InvalidOperationException(),
            };
        });
        var layout = InstallationLayout.FromLocalAppData(localAppData);

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.RolledBack, result.Status);
        Assert.Equal("v1", ReadPointerVersion(layout.CurrentPointerPath));
        Assert.Equal(priorLauncher, File.ReadAllBytes(layout.LauncherPath));
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(
            "Rollback selected the prior version despite a process failure; the prior launcher was restored.",
            result.Reason);
    }

    [Fact]
    public async Task Upgrade_failure_before_pointer_change_restores_launcher_without_rewriting_pointer()
    {
        var priorLauncher = System.Text.Encoding.UTF8.GetBytes("prior-launcher");
        CreateExisting("v1", priorLauncher);
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var priorPointer = File.ReadAllBytes(layout.CurrentPointerPath);
        using var package = Package("v2");
        var runner = new RecordingRunner((_, _, _, _) =>
            Task.FromResult(new ProcessResult(null, "", "", true, false)));

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.RolledBack, result.Status);
        Assert.Equal(priorLauncher, File.ReadAllBytes(layout.LauncherPath));
        Assert.Equal(priorPointer, File.ReadAllBytes(layout.CurrentPointerPath));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Failed_prior_activation_reports_rollback_failure_and_keeps_active_new_version()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"));
        using var package = Package("v2");
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            if (arguments[1] == "v2")
            {
                WritePointer("v2");
            }

            return Task.FromResult(new ProcessResult(9, "", "", false, false));
        });
        var layout = InstallationLayout.FromLocalAppData(localAppData);

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.RollbackFailed, result.Status);
        Assert.Equal("v2", ReadPointerVersion(layout.CurrentPointerPath));
        Assert.True(Directory.Exists(Path.Combine(layout.VersionsRoot, "v2")));
    }

    [Theory]
    [InlineData(false, LocalAiPackageInstallStatus.RolledBack)]
    [InlineData(true, LocalAiPackageInstallStatus.ManualRecoveryRequired)]
    public async Task Fresh_activation_failure_distinguishes_clean_rollback_from_manual_recovery(
        bool pointerChanged,
        LocalAiPackageInstallStatus expected)
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            if (pointerChanged)
            {
                WritePointer("v1");
            }

            return Task.FromResult(new ProcessResult(3, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Status);
        Assert.True(Directory.Exists(Path.Combine(layout.VersionsRoot, "v1")));
        Assert.Equal(
            Path.Combine(layout.VersionsRoot, "v1"),
            result.InactivePublishedVersionPath);
        Assert.Equal(Path.Combine(layout.VersionsRoot, "v1"), result.NewVersionPath);
        Assert.Equal(pointerChanged, File.Exists(layout.LauncherPath));
        Assert.True(Directory.Exists(layout.Root));
    }

    [Fact]
    public async Task Stale_absent_diagnosis_is_not_trusted_when_fresh_inspection_is_unrecognized()
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());
        var installer = new LocalAiPackageInstaller(
            runner,
            new ConstantInspector(new ExistingLocalAiSnapshot(
                ExistingLocalAiState.Unrecognized,
                null,
                null,
                "changed")),
            TimeSpan.FromSeconds(5));
        var stale = new ExistingLocalAiSnapshot(ExistingLocalAiState.Absent, null, null, null);

        var result = await installer.InstallAsync(
            package,
            layout,
            stale,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Refused, result.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Hash_mismatch_during_copy_cleans_owned_temp_and_never_starts_process()
    {
        using var package = Package(
            "v1",
            corruptedFile: LocalAiPackageLayout.VersionRequiredFiles[0]);
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<LocalAiPackageInstallationException>(() =>
            Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
        Assert.False(Directory.Exists(layout.Root));

        using var retryPackage = Package("v1");
        var retryRunner = new RecordingRunner((_, arguments, _, _) =>
        {
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });
        var retry = await Installer(retryRunner).InstallAsync(
            retryPackage,
            layout,
            TestContext.Current.CancellationToken);
        Assert.Equal(LocalAiPackageInstallStatus.Installed, retry.Status);
    }

    [Fact]
    public async Task Cancellation_before_publication_removes_fresh_scaffold_for_retry()
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        using var cancellation = new CancellationTokenSource();
        var inspector = new DelegateInspector(_ =>
        {
            cancellation.Cancel();
            return new ExistingLocalAiSnapshot(ExistingLocalAiState.Absent, null, null, null);
        });
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());
        var installer = new LocalAiPackageInstaller(runner, inspector, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallAsync(package, layout, cancellation.Token));

        Assert.False(Directory.Exists(layout.Root));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Native_publish_failure_removes_fresh_scaffold_and_retry_retains_published_version()
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });
        var acquisition = 0;
        var installer = new LocalAiPackageInstaller(
            runner,
            new ExistingLocalAiInspector(new SystemFileSystemProbe()),
            TimeSpan.FromSeconds(5),
            (candidate, requireFresh) => ++acquisition == 1
                ? InstallationLayoutLease.Acquire(
                    candidate,
                    requireFresh,
                    static (_, _, _) => throw new Win32Exception(5))
                : InstallationLayoutLease.Acquire(candidate, requireFresh));

        await Assert.ThrowsAsync<LocalAiPackageInstallationException>(() =>
            installer.InstallAsync(package, layout, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(layout.Root));
        Assert.Empty(runner.Calls);

        var retry = await installer.InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, retry.Status);
        Assert.Single(runner.Calls);
        Assert.True(Directory.Exists(Path.Combine(layout.VersionsRoot, "v1")));
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            Assert.Equal(
                Content(file),
                File.ReadAllBytes(Path.Combine(layout.VersionsRoot, "v1", file)));
        }
    }

    [Fact]
    public async Task Missing_stable_launcher_in_verified_allowlist_is_rejected_before_mutation()
    {
        using var package = Package("v1", omitFile: LocalAiPackageLayout.StableLauncherFile);
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<LocalAiPackageInstallationException>(() =>
            Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
        Assert.False(Directory.Exists(layout.Root));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("nested/version")]
    public async Task Unsafe_manifest_version_is_rejected_before_mutation(string version)
    {
        using var package = Package(version);
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<LocalAiPackageInstallationException>(() =>
            Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
        Assert.False(Directory.Exists(layout.Root));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"version\":\"v1\",\"extra\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"version\":\"v1\"}")]
    [InlineData("{\"schemaVersion\":1,\"version\":\"../v1\"}")]
    public async Task Noncanonical_pointer_readback_requires_manual_recovery(string pointer)
    {
        using var package = Package("v1");
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            Directory.CreateDirectory(layout.BinRoot);
            File.WriteAllText(layout.CurrentPointerPath, pointer);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.ManualRecoveryRequired, result.Status);
    }

    [Fact]
    public async Task Noncanonical_prior_pointer_is_refused_before_any_mutation()
    {
        var priorLauncher = System.Text.Encoding.UTF8.GetBytes("prior-launcher");
        CreateExisting("v1", priorLauncher);
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var priorPointer = System.Text.Encoding.UTF8.GetBytes(
            "{ \"schemaVersion\": 1, \"version\": \"v1\" }");
        File.WriteAllBytes(layout.CurrentPointerPath, priorPointer);
        using var package = Package("v2");
        var runner = new RecordingRunner((_, _, _, _) => throw new InvalidOperationException());

        var result = await Installer(runner).InstallAsync(
            package,
            layout,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Refused, result.Status);
        Assert.Empty(runner.Calls);
        Assert.Equal(priorPointer, File.ReadAllBytes(layout.CurrentPointerPath));
        Assert.Equal(priorLauncher, File.ReadAllBytes(layout.LauncherPath));
        Assert.False(Directory.Exists(Path.Combine(layout.VersionsRoot, "v2")));
    }

    [Fact]
    public void Installation_layout_rejects_noncanonical_and_reserved_roots()
    {
        Assert.Throws<InstallationLayoutException>(() =>
            InstallationLayout.FromLocalAppData(Path.Combine(localAppData, "child", "..")));
        Assert.Throws<InstallationLayoutException>(() =>
            InstallationLayout.FromLocalAppData(Path.Combine(localAppData, "CON")));
    }

    public void Dispose()
    {
        if (Directory.Exists(localAppData))
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    private VerifiedPackage Package(
        string version,
        string? corruptedFile = null,
        string? omitFile = null)
    {
        var manifest = new ReleaseManifest(
            1,
            version,
            version,
            "signed-7",
            BrokerCompatibilityContract.ProtocolVersion,
            BrokerCompatibilityContract.BuildCompatibilityId,
            new Uri("https://example.invalid/package.zip"),
            1,
            new string('A', 64),
            false,
            []);
        var files = LocalAiPackageLayout.PackageArtifactFiles
            .Append(ReleasePackageVerifier.PackageMetadataFileName)
            .Where(name => !string.Equals(name, omitFile, StringComparison.Ordinal))
            .Select(name => (IRetainedStagingFile)new MemoryRetainedFile(
                name,
                Content(name),
                string.Equals(name, corruptedFile, StringComparison.Ordinal)
                    ? System.Text.Encoding.UTF8.GetBytes("corrupted:" + name)
                    : null))
            .ToArray();
        return new VerifiedPackage(manifest, new MemoryPackageLease(files), files);
    }

    private void WritePointer(string version)
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        Directory.CreateDirectory(layout.BinRoot);
        File.WriteAllBytes(
            layout.CurrentPointerPath,
            System.Text.Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"version\":\"{version}\"}}"));
    }

    private LocalAiPackageInstaller Installer(IProcessRunner runner) => new(
        runner,
        new ExistingLocalAiInspector(new SystemFileSystemProbe()),
        TimeSpan.FromSeconds(5));

    private void CreateExisting(string version, byte[] launcher, bool packageContent = false)
    {
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        using var lease = InstallationLayoutLease.Acquire(layout);
        using var temporary = lease.CreateVersionTemporary();
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            File.WriteAllBytes(
                Path.Combine(temporary.CanonicalPath, file),
                packageContent ? Content(file) : System.Text.Encoding.UTF8.GetBytes("prior:" + file));
        }

        temporary.PublishAbsent(version);
        File.WriteAllBytes(layout.LauncherPath, launcher);
        WritePointer(version);
        lease.Revalidate();
        lease.CommitScaffold();
    }

    private static byte[] Content(string name) =>
        System.Text.Encoding.UTF8.GetBytes("verified:" + name);

    private void AssertInstalledVersionFilesLocked(InstallationLayout layout, string version)
    {
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            var path = Path.Combine(layout.VersionsRoot, version, file);
            Assert.True(Record.Exception(() => File.WriteAllText(path, "tampered"))
                is IOException or UnauthorizedAccessException);
            Assert.True(Record.Exception(() => File.Delete(path))
                is IOException or UnauthorizedAccessException);
            var replacement = Path.Combine(
                localAppData,
                "replacement-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(replacement, "replacement");
            Assert.True(Record.Exception(() => File.Move(replacement, path, overwrite: true))
                is IOException or UnauthorizedAccessException);
            File.Delete(replacement);
        }
    }

    private static string ReadPointerVersion(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("version").GetString()!;
    }

    private sealed class RecordingRunner(
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessResult>> run)
        : IProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add((executable, arguments.ToArray(), timeout));
            return run(executable, arguments, timeout, cancellationToken);
        }
    }

    private sealed class MemoryRetainedFile : IRetainedStagingFile
    {
        private readonly byte[] readBytes;

        public MemoryRetainedFile(string path, byte[] bytes, byte[]? readBytes = null)
        {
            this.readBytes = readBytes ?? bytes;
            Metadata = new VerifiedPackageFile(
                path,
                bytes.LongLength,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        }

        public VerifiedPackageFile Metadata { get; }
        public void Revalidate() { }
        public Stream OpenRead() => new MemoryStream(readBytes, writable: false);
        public byte[] ReadAllBytes(int maximumBytes) => readBytes.ToArray();
        public void Dispose() { }
    }

    private sealed class ConstantInspector(ExistingLocalAiSnapshot snapshot) : IExistingLocalAiInspector
    {
        public ExistingLocalAiSnapshot Inspect(string localAppData) => snapshot;
    }

    private sealed class DelegateInspector(
        Func<string, ExistingLocalAiSnapshot> inspect) : IExistingLocalAiInspector
    {
        public ExistingLocalAiSnapshot Inspect(string localAppData) => inspect(localAppData);
    }

    private sealed class RecordingInspector(
        IExistingLocalAiInspector inner,
        Action? onInspect = null) : IExistingLocalAiInspector
    {
        private int callCount;
        public int CallCount => Volatile.Read(ref callCount);

        public ExistingLocalAiSnapshot Inspect(string localAppData)
        {
            Interlocked.Increment(ref callCount);
            onInspect?.Invoke();
            return inner.Inspect(localAppData);
        }
    }

    private sealed class MemoryPackageLease(IReadOnlyList<IRetainedStagingFile> files) : IStagingRootLease
    {
        public string CanonicalPath => @"C:\retained-package";
        public void Revalidate() { }
        public void ValidateCreatedFile(Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle, string expectedPath) { }
        public IRetainedStagingFile RetainFile(string relativePath) =>
            files.Single(file => file.Metadata.RelativePath == relativePath);
        public void ValidateExactLayout(IEnumerable<string> approvedRelativePaths) =>
            Assert.Equal(
                files.Select(file => file.Metadata.RelativePath).Order(),
                approvedRelativePaths.Order());
        public void Cleanup() { }
        public void Dispose() { }
    }

    private sealed class ThrowingSecureMutexFactory(Exception exception)
        : ISecureNamedMutexFactory
    {
        public ISecureNamedMutex Create(string name) => throw exception;
    }
}
