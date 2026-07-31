using LocalAi.Installer.Core.Diagnosis;

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
        var path = @"C:\Windows\notepad.exe";
        var probe = CreateProbe(
            new UninstallEntrySnapshot(
                "ollama",
                "Ollama",
                "0.11.4",
                null,
                path,
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
    }

    private static WindowsInstalledApplicationProbe CreateProbe(
        UninstallEntrySnapshot entry,
        params ExecutableIdentitySnapshot[] identities) =>
        new(
            new FakeUninstallEntrySource([entry]),
            new FakeExecutableIdentityProbe(identities));

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
}
