using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;
using System.Runtime.Versioning;

namespace LocalAi.Installer.Core.Tests;

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
        var runner = new RecordingRunner((_, _, _, _) =>
        {
            WritePointer("v1");
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });
        var layout = InstallationLayout.FromLocalAppData(localAppData);
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
        Assert.Equal(["activate", "v1", "--stop-running"], call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
    }

    [Fact]
    public async Task Compatible_upgrade_backs_up_launcher_and_activates_new_version()
    {
        var priorLauncher = System.Text.Encoding.UTF8.GetBytes("prior-launcher");
        CreateExisting("v1", priorLauncher);
        using var package = Package("v2");
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });
        var layout = InstallationLayout.FromLocalAppData(localAppData);
        var installer = Installer(runner);

        var result = await installer.InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.Installed, result.Status);
        Assert.Equal("v1", result.PriorVersion);
        Assert.NotNull(result.LauncherBackupPath);
        Assert.Equal(priorLauncher, File.ReadAllBytes(result.LauncherBackupPath));
        Assert.Equal(priorLauncher.LongLength, result.LauncherBackup!.Length);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(priorLauncher)),
            result.LauncherBackup.Sha256);
        Assert.Equal(Content(LocalAiPackageLayout.StableLauncherFile), File.ReadAllBytes(layout.LauncherPath));
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

        Assert.Throws<LocalAiPackageInstallationException>(() =>
            InstallationLayoutLease.Acquire(layout));

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
    public async Task Exact_existing_version_is_idempotent_but_still_updates_launcher()
    {
        CreateExisting("v1", System.Text.Encoding.UTF8.GetBytes("prior-launcher"), packageContent: true);
        using var package = Package("v1");
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });
        var layout = InstallationLayout.FromLocalAppData(localAppData);

        var result = await Installer(runner).InstallAsync(package, layout, TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageInstallStatus.AlreadyInstalled, result.Status);
        Assert.Single(runner.Calls);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(layout.VersionsRoot),
            path => Path.GetFileName(path).StartsWith(".install-", StringComparison.Ordinal));
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
        Directory.CreateDirectory(target);
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            File.WriteAllBytes(Path.Combine(target, file), Content(file));
        }
        File.WriteAllText(Path.Combine(target, LocalAiPackageLayout.VersionRequiredFiles[0]), "mismatch");
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
        var runner = new RecordingRunner((executable, arguments, _, _) =>
        {
            attempt++;
            Assert.Equal(layout.LauncherPath, executable);
            if (attempt == 1)
            {
                WritePointer("v2");
                return Task.FromResult(new ProcessResult(17, "", "", false, false));
            }

            Assert.Equal(priorLauncher, File.ReadAllBytes(layout.LauncherPath));
            Assert.Equal(["activate", "v1", "--stop-running"], arguments);
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
        Assert.Empty(Directory.EnumerateDirectories(layout.VersionsRoot));
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
        var versionPath = Path.Combine(layout.VersionsRoot, version);
        Directory.CreateDirectory(versionPath);
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            File.WriteAllBytes(
                Path.Combine(versionPath, file),
                packageContent ? Content(file) : System.Text.Encoding.UTF8.GetBytes("prior:" + file));
        }

        Directory.CreateDirectory(layout.LauncherDirectory);
        File.WriteAllBytes(layout.LauncherPath, launcher);
        WritePointer(version);
    }

    private static byte[] Content(string name) =>
        System.Text.Encoding.UTF8.GetBytes("verified:" + name);

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
}
