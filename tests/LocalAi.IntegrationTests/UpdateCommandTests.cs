using System.Text;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The decisions `localai update` makes before it installs anything.
///
/// Downloading, verifying and activating a release is not retested here — that is
/// ReleaseInstallService's, and it is covered where it lives. What is covered here is
/// everything this command adds: whether an update is needed at all, whether now is a moment
/// that would abandon somebody's work, and what happens when the answer from the network
/// cannot be believed.
/// </summary>
public sealed class UpdateCommandTests : IDisposable
{
    private const string Directory50 = "be08af033a2a";
    private const string Directory51 = "467ed5f0f9bf";

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-update-cmd-" + Guid.NewGuid().ToString("N"));

    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public UpdateCommandTests() => Install(Directory50, "0.1.50");

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task An_installation_that_is_current_is_left_alone()
    {
        var exit = await Run(new StubFeed("v0.1.50", "0.1.50", Directory50));

        Assert.Equal(0, exit);
        Assert.Contains(
            "0.1.50 is already the newest release",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newer_release_is_taken_up_to_the_point_of_installing_it()
    {
        var feed = new StubFeed("v0.1.51", "0.1.51", Directory51)
        {
            OnDownload = () => throw new ReleaseResolutionException("the package never arrived"),
        };

        var exit = await Run(feed);

        Assert.Equal(1, exit);
        Assert.Contains(
            "LocalAi 0.1.51 is available; this installation is 0.1.50",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("the package never arrived", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordering by version rather than by text, in the place where getting it wrong would
    /// refuse the update somebody actually needs.
    /// </summary>
    [Fact]
    public async Task Being_on_0_1_9_counts_as_behind_0_1_10()
    {
        Install(Directory50, "0.1.9");
        var feed = new StubFeed("v0.1.10", "0.1.10", Directory51)
        {
            OnDownload = () => throw new ReleaseResolutionException("stopped here"),
        };

        await Run(feed);

        Assert.Contains(
            "LocalAi 0.1.10 is available",
            output.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// What cannot be proven is not installed. The feed raises the same refusal it raises for
    /// a real manifest signed by a stranger, and the command stops there.
    /// </summary>
    [Fact]
    public async Task A_release_that_does_not_verify_is_refused()
    {
        var feed = new StubFeed("v0.1.51", "0.1.51", Directory51)
        {
            OnResolve = () => throw new ReleaseResolutionException(
                "The manifest for release 'v0.1.51' failed verification and will not be used."),
        };

        var exit = await Run(feed);

        Assert.Equal(1, exit);
        Assert.Contains("failed verification", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_with_nothing_installed_is_sent_to_the_installer()
    {
        Directory.Delete(Path.Combine(root, "bin"), recursive: true);

        var exit = await Run(new StubFeed("v0.1.51", "0.1.51", Directory51));

        Assert.Equal(1, exit);
        Assert.Contains("no LocalAi installation", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Activation stops the tools running out of the current version, so updating underneath
    /// queued work would abandon somebody's inference halfway. A person who typed one command
    /// did not ask to lose a job.
    /// </summary>
    [Fact]
    public async Task Queued_work_refuses_the_update_and_says_how_to_wait_for_it()
    {
        Queue(2);

        var exit = await Run(new StubFeed("v0.1.51", "0.1.51", Directory51));

        Assert.Equal(2, exit);
        Assert.Contains("2 queued job(s)", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("localai update --wait", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_option_changes_nothing()
    {
        var exit = await Run(new StubFeed("v0.1.51", "0.1.51", Directory51), "--yes-please");

        Assert.Equal(2, exit);
        Assert.Contains("Unknown option '--yes-please'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_help_says_what_is_not_touched()
    {
        var exit = await Run(new StubFeed("v0.1.50", "0.1.50", Directory50), "--help");

        Assert.Equal(2, exit);
        Assert.Contains("Prerequisites, models and client integrations are not touched",
            error.ToString(),
            StringComparison.Ordinal);
    }

    private Task<int> Run(IReleaseFeed feed, params string[] args) =>
        UpdateCommand.ExecuteAsync(
            args,
            root,
            output,
            error,
            feed,
            processRunner: null,
            TestContext.Current.CancellationToken);

    /// <summary>Real queued jobs, written by the queue the command asks.</summary>
    private void Queue(int count)
    {
        var queue = new LocalAi.Broker.DurableQueue(root);
        for (var index = 0; index < count; index++)
        {
            queue.EnqueueAsync(LocalJobRequestFactory.CreateEmbed(
                    "update-test-" + index,
                    LocalJobPriority.Background,
                    "test-model",
                    ["input"]))
                .GetAwaiter()
                .GetResult();
        }
    }

    /// <summary>
    /// The pointer names a version directory, as LocalAiPackageInstaller writes it, and a
    /// separate record says which release that directory came from (#255).
    /// </summary>
    private void Install(string directory, string release)
    {
        var binRoot = Path.Combine(root, "bin");
        Directory.CreateDirectory(binRoot);
        File.WriteAllBytes(
            Path.Combine(binRoot, "current.json"),
            CurrentPointerSnapshot.CreateCanonicalBytes(directory));
        new InstalledReleaseStore(binRoot).Write(directory, release);
    }

    /// <summary>
    /// A feed that answers with a manifest and nothing else. Everything past the manifest is
    /// ReleaseInstallService's, and these tests stop where its own do begin.
    /// </summary>
    private sealed class StubFeed(string tag, string version, string versionDirectory)
        : IReleaseFeed
    {
        public Action? OnResolve { get; init; }

        public Action? OnDownload { get; init; }

        public Task<string> ResolveTagAsync(
            string requestedTag,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(tag);

        public Task<ResolvedRelease> ResolveAsync(
            string requestedTag,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            OnResolve?.Invoke();
            var manifest = new ReleaseManifest(
                1,
                version,
                versionDirectory,
                "signed-7",
                BrokerCompatibilityContract.ProtocolVersion,
                BrokerCompatibilityContract.BuildCompatibilityId,
                new Uri("https://releases.example.invalid/localai-" + version + ".zip"),
                1024,
                new string('a', 64),
                false,
                []);
            return Task.FromResult(new ResolvedRelease(manifest, [], []));
        }

        public Task<string> DownloadPackageAsync(
            string requestedTag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded = null,
            CancellationToken cancellationToken = default)
        {
            OnDownload?.Invoke();
            return Task.FromResult(Path.Combine(workingDirectory, "localai-package.zip"));
        }
    }
}
