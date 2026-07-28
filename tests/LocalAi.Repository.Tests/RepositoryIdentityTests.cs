using LocalAi.Repository;

namespace LocalAi.Repository.Tests;

public sealed class RepositoryIdentityTests
{
    [Fact]
    public void Same_common_directory_has_same_identity_regardless_of_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo", ".git");

        var first = RepositoryIdentity.FromCommonDirectory(root);
        var second = RepositoryIdentity.FromCommonDirectory(
            Path.Combine(root, "..", ".git"));

        Assert.Equal(first, second);
        Assert.Equal(64, first.Id.Length);
    }
}
