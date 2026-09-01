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
        Assert.StartsWith("NOT_CONFIGURED", status.Message, StringComparison.Ordinal);
        Assert.Contains("CodeSearch", status.Message);
        Assert.Contains("LocalLm", status.Message);
        Assert.Contains("Claude MCP", status.Message);
        Assert.Contains("Codex MCP", status.Message);
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public void Status_names_the_repository_it_is_answering_about()
    {
        // The caller passes a path, a worktree or nothing at all, and every one of those resolves
        // to a common directory that may not be the one they meant — which is how #94 was found.
        // A verdict that does not say which repository it is about cannot be checked.
        var status = RepoCommand.Status(
            Path.Combine(_root, "repo.git"),
            Path.Combine(_root, "runtime"));

        Assert.Contains(status.Identity.CommonDirectory, status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_requires_explicit_acceptance()
    {
        Assert.Throws<InvalidOperationException>(
            () => BootstrapCommand.RequireAcceptance(accept: false));
    }

    [Fact]
    public void Bootstrap_plans_model_sync_only_through_mcp()
    {
        var plan = BootstrapCommand.Plan(
            Path.Combine(_root, "repo.git"),
            Path.Combine(_root, "runtime"),
            Path.Combine(_root, "install"));

        Assert.Contains(
            plan.Changes,
            change => change.Contains("local_models_sync", StringComparison.Ordinal));
        Assert.DoesNotContain(
            plan.Changes,
            change => change.Contains("ollama pull", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Configured_repository_does_not_offer_bootstrap_again()
    {
        var common = Path.Combine(_root, "repo.git");
        var runtime = Path.Combine(_root, "runtime");
        var status = RepoCommand.Status(common, runtime);
        var repositoryRoot = Path.Combine(runtime, "repositories", status.Identity.Id);
        new RepositoryManifestStore(FsPath.From(repositoryRoot)).Save(new RepositoryManifest(
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
