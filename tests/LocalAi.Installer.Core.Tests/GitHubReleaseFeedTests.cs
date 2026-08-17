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
        Action<string, string>? onDownload = null,
        string standardOutput = "",
        Func<IReadOnlyList<string>, ProcessResult>? onRun = null,
        Func<IReadOnlyList<string>, string, ProcessResult>? onFileRun = null)
        : IProcessRunner, IProcessFileRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];
        public List<(IReadOnlyList<string> Arguments, string OutputPath)> FileInvocations
        { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Invocations.Add(arguments);
            var result = onRun?.Invoke(arguments) ??
                new ProcessResult(exitCode, standardOutput, standardError, false, false);
            if (result.ExitCode == 0 && arguments.Contains("download"))
            {
                onDownload?.Invoke(
                    ValueAfter(arguments, "--dir"),
                    ValueAfter(arguments, "--pattern"));
            }

            return Task.FromResult(result);
        }

        public Task<ProcessResult> RunToFileAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string outputPath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            FileInvocations.Add((arguments, outputPath));
            return Task.FromResult(
                onFileRun?.Invoke(arguments, outputPath) ??
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.EndsWith(GitHubReleaseFeed.PackageAsset, path, StringComparison.Ordinal);
        Assert.Equal(
            GitHubReleaseFeed.PackageAsset,
            ValueAfter(cli.Invocations.Single(), "--pattern"));
    }

    [Fact]
    public async Task A_failed_tag_download_falls_back_to_the_exact_asset_database_id()
    {
        byte[] package = [0, 255, 1, 254, 2];
        var cli = new FakeCli(
            0,
            onRun: arguments => arguments.Contains("download")
                ? new ProcessResult(1, string.Empty, "release not found", false, false)
                : arguments.Contains("graphql")
                    ? new ProcessResult(
                        0,
                        "{\"data\":{\"repository\":{\"release\":{\"databaseId\":371801682}}}}",
                        string.Empty,
                        false,
                        false)
                    : new ProcessResult(
                    0,
                    "[{\"id\":518217710,\"name\":\"" +
                    GitHubReleaseFeed.PackageAsset + "\",\"size\":" + package.Length +
                    "}]",
                    string.Empty,
                    false,
                    false),
            onFileRun: (_, outputPath) =>
            {
                File.WriteAllBytes(outputPath, package);
                return new ProcessResult(0, string.Empty, string.Empty, false, false);
            });
        var feed = new GitHubReleaseFeed(cli);

        var path = await feed.DownloadPackageAsync(
            "0.1.37",
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(package, await File.ReadAllBytesAsync(
            path,
            TestContext.Current.CancellationToken));
        var invocation = Assert.Single(cli.FileInvocations);
        Assert.Contains(
            "repos/MrAliev/local-ai-developer-toolkit/releases/assets/518217710",
            invocation.Arguments);
        Assert.Equal(path, invocation.OutputPath);
    }

    [Fact]
    public async Task The_fallback_refuses_a_different_asset_name_without_downloading_it()
    {
        var cli = new FakeCli(
            0,
            onRun: arguments => arguments.Contains("download")
                ? new ProcessResult(1, string.Empty, "release not found", false, false)
                : arguments.Contains("graphql")
                    ? new ProcessResult(
                        0,
                        "{\"data\":{\"repository\":{\"release\":{\"databaseId\":7}}}}",
                        string.Empty,
                        false,
                        false)
                    : new ProcessResult(
                        0,
                        "[{\"id\":42,\"name\":\"different.zip\",\"size\":10}]",
                        string.Empty,
                        false,
                        false));
        var feed = new GitHubReleaseFeed(cli);

        var error = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.DownloadPackageAsync(
                "0.1.37",
                root,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not publish", error.Message, StringComparison.Ordinal);
        Assert.Empty(cli.FileInvocations);
    }

    [Fact]
    public async Task The_fallback_deletes_a_download_whose_size_differs_from_GitHub_metadata()
    {
        var cli = new FakeCli(
            0,
            onRun: arguments => arguments.Contains("download")
                ? new ProcessResult(1, string.Empty, "release not found", false, false)
                : arguments.Contains("graphql")
                    ? new ProcessResult(
                        0,
                        "{\"data\":{\"repository\":{\"release\":{\"databaseId\":7}}}}",
                        string.Empty,
                        false,
                        false)
                    : new ProcessResult(
                        0,
                        "[{\"id\":42,\"name\":\"" +
                        GitHubReleaseFeed.PackageAsset + "\",\"size\":100}]",
                        string.Empty,
                        false,
                        false),
            onFileRun: (_, outputPath) =>
            {
                File.WriteAllBytes(outputPath, [1, 2, 3]);
                return new ProcessResult(0, string.Empty, string.Empty, false, false);
            });
        var feed = new GitHubReleaseFeed(cli);

        var error = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.DownloadPackageAsync(
                "0.1.37",
                root,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("GitHub reports 100", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, GitHubReleaseFeed.PackageAsset)));
    }

    [Fact]
    public async Task Cancelling_the_fallback_deletes_its_partial_download()
    {
        using var cancellation = new CancellationTokenSource();
        var cli = new FakeCli(
            0,
            onRun: arguments => arguments.Contains("download")
                ? new ProcessResult(1, string.Empty, "release not found", false, false)
                : arguments.Contains("graphql")
                    ? new ProcessResult(
                        0,
                        "{\"data\":{\"repository\":{\"release\":{\"databaseId\":7}}}}",
                        string.Empty,
                        false,
                        false)
                    : new ProcessResult(
                        0,
                        "[{\"id\":42,\"name\":\"" +
                        GitHubReleaseFeed.PackageAsset + "\",\"size\":100}]",
                        string.Empty,
                        false,
                        false),
            onFileRun: (_, outputPath) =>
            {
                File.WriteAllBytes(outputPath, [1, 2, 3]);
                cancellation.Cancel();
                return new ProcessResult(null, string.Empty, string.Empty, false, true);
            });
        var feed = new GitHubReleaseFeed(cli);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => feed.DownloadPackageAsync("0.1.37", root, cancellationToken: cancellation.Token));

        Assert.False(File.Exists(Path.Combine(root, GitHubReleaseFeed.PackageAsset)));
    }

    [Fact]
    public async Task An_explicit_tag_is_used_as_given()
    {
        var cli = new FakeCli(0);
        var feed = new GitHubReleaseFeed(cli);

        Assert.Equal(
            "0.1.2",
            await feed.ResolveTagAsync("  0.1.2 ", TestContext.Current.CancellationToken));
        Assert.Empty(cli.Invocations);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("LATEST")]
    [InlineData("")]
    public async Task Latest_is_resolved_to_the_newest_published_tag(string requested)
    {
        // "latest" is not a tag GitHub serves; asking for it by name returns 404, which is
        // exactly how a default-looking field used to break the whole install.
        var cli = new FakeCli(0, standardOutput: "0.1.7\n");
        var feed = new GitHubReleaseFeed(cli);

        Assert.Equal(
            "0.1.7",
            await feed.ResolveTagAsync(requested, TestContext.Current.CancellationToken));
        Assert.Contains("view", cli.Invocations.Single());
    }

    [Fact]
    public async Task Latest_falls_back_to_the_GraphQL_backed_release_list()
    {
        var cli = new FakeCli(
            0,
            onRun: arguments => arguments.Contains("view")
                ? new ProcessResult(1, string.Empty, "release not found", false, false)
                : new ProcessResult(0, "0.1.37\n", string.Empty, false, false));
        var feed = new GitHubReleaseFeed(cli);

        Assert.Equal(
            "0.1.37",
            await feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));
        Assert.Equal(2, cli.Invocations.Count);
        Assert.Contains("list", cli.Invocations[1]);
    }

    [Fact]
    public async Task A_failure_to_determine_the_newest_release_is_reported()
    {
        var feed = new GitHubReleaseFeed(new FakeCli(1, "not logged in"));

        var error = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Contains("gh auth login", error.Message, StringComparison.Ordinal);
    }
}
