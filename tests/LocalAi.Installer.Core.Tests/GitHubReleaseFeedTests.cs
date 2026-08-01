using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class GitHubReleaseFeedTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.ReleaseFeedTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string flag)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], flag, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        throw new InvalidOperationException($"Argument '{flag}' was not passed.");
    }

    private sealed class FakeCli(
        int exitCode,
        string standardError = "",
        Action<string, string>? onDownload = null) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Invocations.Add(arguments);
            if (exitCode == 0)
            {
                onDownload?.Invoke(
                    ValueAfter(arguments, "--dir"),
                    ValueAfter(arguments, "--pattern"));
            }

            return Task.FromResult(
                new ProcessResult(exitCode, string.Empty, standardError, false, false));
        }
    }

    private sealed class ThrowingCli : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new System.ComponentModel.Win32Exception("not found");
    }

    [Fact]
    public async Task A_missing_github_cli_is_explained_rather_than_reported_as_a_download_error()
    {
        var feed = new GitHubReleaseFeed(new ThrowingCli());

        var error = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.2", root, TestContext.Current.CancellationToken));

        Assert.Contains("gh auth login", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_download_points_at_sign_in_and_keeps_the_cli_output()
    {
        var feed = new GitHubReleaseFeed(new FakeCli(1, "HTTP 404: Not Found"));

        var error = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.2", root, TestContext.Current.CancellationToken));

        Assert.Contains("gh auth login", error.Message, StringComparison.Ordinal);
        Assert.Contains("404", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_asset_the_release_does_not_publish_is_reported()
    {
        // The CLI succeeds but writes nothing: the release simply has no such asset.
        var feed = new GitHubReleaseFeed(new FakeCli(0));

        var error = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.2", root, TestContext.Current.CancellationToken));

        Assert.Contains("does not publish", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manifest_not_signed_by_the_trusted_key_is_refused()
    {
        var cli = new FakeCli(0, onDownload: (directory, pattern) =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, pattern),
                pattern.EndsWith(".sig", StringComparison.Ordinal)
                    ? new byte[64]
                    : System.Text.Encoding.UTF8.GetBytes("""{"SchemaVersion":1}"""));
        });
        var feed = new GitHubReleaseFeed(cli);

        var error = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.2", root, TestContext.Current.CancellationToken));

        Assert.Contains("failed verification", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_the_manifest_and_its_signature_are_requested_by_name()
    {
        var cli = new FakeCli(0);
        var feed = new GitHubReleaseFeed(cli);

        await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.2", root, TestContext.Current.CancellationToken));

        var patterns = cli.Invocations
            .Select(arguments => ValueAfter(arguments, "--pattern"))
            .ToArray();
        Assert.Contains(GitHubReleaseFeed.ManifestAsset, patterns);
        Assert.Contains(GitHubReleaseFeed.SignatureAsset, patterns);
    }

    [Fact]
    public async Task The_package_download_targets_the_package_asset()
    {
        var cli = new FakeCli(0);
        var feed = new GitHubReleaseFeed(cli);

        var path = await feed.DownloadPackageAsync(
            "0.1.2",
            root,
            TestContext.Current.CancellationToken);

        Assert.EndsWith(GitHubReleaseFeed.PackageAsset, path, StringComparison.Ordinal);
        Assert.Equal(
            GitHubReleaseFeed.PackageAsset,
            ValueAfter(cli.Invocations.Single(), "--pattern"));
    }
}
