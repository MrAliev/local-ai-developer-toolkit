using LocalAi.Installer.Core.Diagnosis;
using System.Text.RegularExpressions;

namespace LocalAi.Installer.Core.Tests;

public sealed class WindowsInstalledApplicationProbeTests
{
    [Fact]
    public async Task Rejects_matching_display_entry_without_an_executable()
    {
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                "Ollama",
                "0.11.4",
                null,
                null,
                null));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_matching_display_entry_whose_executable_is_missing()
    {
        var path = @"C:\Stale\ollama.exe";
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                "Ollama",
                "0.11.4",
                @"C:\Stale",
                path,
                null),
            ExecutableIdentitySnapshot.Absent(path));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_stale_uninstall_string()
    {
        var path = @"C:\Removed\ollama.exe";
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                "Ollama",
                "0.11.4",
                null,
                null,
                $"\"{path}\" uninstall"),
            ExecutableIdentitySnapshot.Absent(path));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_unrelated_executable_identity()
    {
        var path = @"C:\Fake\Ollama\ollama.exe";
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                "Ollama",
                "0.11.4",
                @"C:\Fake\Ollama",
                null,
                null),
            new ExecutableIdentitySnapshot(
                path,
                true,
                "10.0.0.0",
                "Microsoft Windows",
                "notepad.exe"));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Accepts_exact_entry_with_existing_versioned_Ollama_identity()
    {
        var path = @"C:\Programs\Ollama\ollama.exe";
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                "Ollama",
                "0.11.4",
                @"C:\Programs\Ollama",
                $"\"{path}\",0",
                null),
            new ExecutableIdentitySnapshot(
                path,
                true,
                "0.11.4.0",
                "Ollama",
                "ollama.exe"));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(path, result.ExecutablePath);
        Assert.Equal("0.11.4.0", result.ExecutableVersion);
        Assert.Equal("0.11.4", result.DetectedVersion);
    }

    [Fact]
    public async Task Accepts_official_versioned_display_name_with_blank_PE_metadata()
    {
        var directory = @"C:\Users\person\AppData\Local\Programs\Ollama";
        var path = Path.Combine(directory, "ollama.exe");
        var uninstaller = Path.Combine(directory, "unins000.exe");
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "{official}_is1",
                "Ollama version 0.32.5",
                "0.32.5",
                directory + Path.DirectorySeparatorChar,
                uninstaller,
                $"\"{uninstaller}\""),
            new ExecutableIdentitySnapshot(
                path,
                true,
                null,
                null,
                null));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(path, result.ExecutablePath);
        Assert.Equal("0.32.5", result.DetectedVersion);
    }

    [Fact]
    public async Task Falls_back_to_validated_display_name_version_suffix()
    {
        var directory = @"C:\Programs\Ollama";
        var path = Path.Combine(directory, "ollama.exe");
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                "Ollama version 1.2.3-rc.1",
                null,
                directory,
                null,
                null),
            new ExecutableIdentitySnapshot(path, true, null, null, null));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("1.2.3-rc.1", result.DetectedVersion);
    }

    [Fact]
    public async Task Falls_back_to_validated_file_version_for_exact_display_name()
    {
        var directory = @"C:\Programs\Ollama";
        var path = Path.Combine(directory, "ollama.exe");
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                "Ollama",
                null,
                directory,
                null,
                null),
            new ExecutableIdentitySnapshot(
                path,
                true,
                "2.3.4.0",
                "Ollama",
                "ollama.exe"));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("2.3.4.0", result.DetectedVersion);
    }

    [Theory]
    [InlineData("Ollama version ", null)]
    [InlineData("Ollama version latest", null)]
    [InlineData("Ollama version 0.32.5 trailing", "0.32.5")]
    [InlineData("Ollama version 0.32.5.6.7", null)]
    [InlineData("Ollama Preview", "0.32.5")]
    [InlineData("Ollama version 999999.1.1", null)]
    public async Task Rejects_unparseable_versions_and_display_name_prefix_tricks(
        string displayName,
        string? displayVersion)
    {
        var directory = @"C:\Programs\Ollama";
        var path = Path.Combine(directory, "ollama.exe");
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                displayName,
                displayVersion,
                directory,
                null,
                null),
            new ExecutableIdentitySnapshot(path, true, null, null, null));

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Production_probe_recognizes_present_supported_official_entry()
    {
        var entries = new WindowsRegistryUninstallEntrySource().ReadEntries(
            TestContext.Current.CancellationToken);
        var presentOfficialEntry = entries.FirstOrDefault(entry =>
            IsSupportedDisplayName(entry.DisplayName) &&
            !string.IsNullOrWhiteSpace(entry.InstallLocation) &&
            File.Exists(Path.Combine(entry.InstallLocation, "ollama.exe")));
        if (presentOfficialEntry is null)
        {
            Assert.Skip("No supported installed Ollama entry is present.");
            return;
        }

        var result = await new WindowsInstalledApplicationProbe().FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(
            Path.GetFullPath(
                Path.Combine(presentOfficialEntry.InstallLocation!, "ollama.exe")),
            result.ExecutablePath,
            ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(result.DetectedVersion));
    }

    [Fact]
    public async Task Rejects_candidate_whose_physical_path_escapes_entry_directory()
    {
        var directory = @"C:\Trusted\Ollama";
        var path = Path.Combine(directory, "ollama.exe");
        var probe = new WindowsInstalledApplicationProbe(
            new FakeUninstallEntrySource(
            [
                new UninstallEntrySnapshot(
                    "ollama",
                    "Ollama",
                    "0.32.5",
                    directory,
                    null,
                    null),
            ]),
            new FakeExecutableIdentityProbe(
            [
                new ExecutableIdentitySnapshot(path, true, null, null, null),
            ]),
            new FakePhysicalPathResolver(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [directory] = directory,
                    [path] = @"C:\Outside\ollama.exe",
                }),
            []);

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Accepts_existing_executable_in_explicitly_approved_official_directory()
    {
        var directory = @"C:\Program Files\Ollama";
        var path = Path.Combine(directory, "ollama.exe");
        var probe = new WindowsInstalledApplicationProbe(
            new FakeUninstallEntrySource(
            [
                new UninstallEntrySnapshot(
                    "ollama",
                    "Ollama version 0.32.5",
                    "0.32.5",
                    null,
                    null,
                    null),
            ]),
            new FakeExecutableIdentityProbe(
            [
                new ExecutableIdentitySnapshot(path, true, null, null, null),
            ]),
            new FakePhysicalPathResolver(),
            [directory]);

        var result = await probe.FindOllamaAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(path, result.ExecutablePath);
    }

    private static WindowsInstalledApplicationProbe CreateProbe(
        UninstallEntrySnapshot entry,
        params ExecutableIdentitySnapshot[] identities) =>
        new(
            new FakeUninstallEntrySource([entry]),
            new FakeExecutableIdentityProbe(identities),
            new FakePhysicalPathResolver(),
            []);

    private static bool IsSupportedDisplayName(string? displayName) =>
        string.Equals(displayName, "Ollama", StringComparison.OrdinalIgnoreCase) ||
        (displayName is not null &&
         Regex.IsMatch(
             displayName,
             @"^Ollama version [0-9]{1,5}(?:\.[0-9]{1,5}){1,3}(?:[-+][0-9A-Za-z](?:[0-9A-Za-z.-]{0,30}[0-9A-Za-z])?)?$",
             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
             TimeSpan.FromMilliseconds(100)));

    private sealed class FakeUninstallEntrySource(
        IReadOnlyList<UninstallEntrySnapshot> entries) : IUninstallEntrySource
    {
        public IReadOnlyList<UninstallEntrySnapshot> ReadEntries(
            CancellationToken cancellationToken) => entries;
    }

    private sealed class FakeExecutableIdentityProbe(
        IEnumerable<ExecutableIdentitySnapshot> identities) : IExecutableIdentityProbe
    {
        private readonly Dictionary<string, ExecutableIdentitySnapshot> _identities =
            identities.ToDictionary(
                identity => identity.Path,
                StringComparer.OrdinalIgnoreCase);

        public ExecutableIdentitySnapshot Inspect(string path) =>
            _identities.GetValueOrDefault(path) ??
            ExecutableIdentitySnapshot.Absent(path);
    }

    private sealed class FakePhysicalPathResolver(
        IReadOnlyDictionary<string, string>? physicalPaths = null)
        : IPhysicalPathResolver
    {
        public string ResolvePhysicalPath(string path) =>
            physicalPaths?.GetValueOrDefault(path) ?? Path.GetFullPath(path);
    }
}
