using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

public sealed class BootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-bootstrap-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Unconfigured_status_is_read_only_and_offers_every_tool()
    {
        var runtime = Path.Combine(_root, "runtime");

        var status = RepoCommand.Status(
            Path.Combine(_root, "repo.git"),
            runtime);

        Assert.False(status.Configured);
        Assert.Contains("NOT_CONFIGURED", status.Message);
        Assert.Contains("CodeSearch", status.Message);
        Assert.Contains("LocalLm", status.Message);
        Assert.Contains("Claude MCP", status.Message);
        Assert.Contains("Codex MCP", status.Message);
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public void Bootstrap_requires_explicit_acceptance()
    {
        Assert.Throws<InvalidOperationException>(
            () => BootstrapCommand.RequireAcceptance(accept: false));
    }

    [Fact]
    public void Configured_repository_does_not_offer_bootstrap_again()
    {
        var common = Path.Combine(_root, "repo.git");
        var runtime = Path.Combine(_root, "runtime");
        var status = RepoCommand.Status(common, runtime);
        var repositoryRoot = Path.Combine(runtime, "repositories", status.Identity.Id);
        new RepositoryManifestStore(repositoryRoot).Save(new RepositoryManifest(
            status.Identity.Id,
            status.Identity.CommonDirectory,
            "refs/heads/dev",
            "generation",
            "tree",
            "model",
            1,
            1,
            3,
            RepositoryIndexState.Current,
            [],
            DateTimeOffset.UtcNow));

        var plan = BootstrapCommand.Plan(common, runtime, _root);

        Assert.True(plan.Repository.Configured);
        Assert.Single(plan.Changes);
        Assert.Contains("already configured", plan.Changes[0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
