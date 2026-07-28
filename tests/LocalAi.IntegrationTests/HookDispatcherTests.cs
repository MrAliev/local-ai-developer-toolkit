using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

public sealed class HookDispatcherTests
{
    [Theory]
    [InlineData(24, HookDispatchMode.Synchronous)]
    [InlineData(25, HookDispatchMode.Queued)]
    public void Small_delta_is_synchronous_and_large_delta_is_queued(
        int chunks,
        HookDispatchMode expected)
    {
        var plan = HookCommand.Plan(
            RepositoryHookEvent.PostCommit,
            "repository",
            "tree",
            chunks);

        Assert.Equal(expected, plan.Mode);
        Assert.Equal("repository:repository:tree:tree", plan.DeduplicationKey);
    }
}
