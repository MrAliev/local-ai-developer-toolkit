using LocalAi.Contracts;
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

    /// <summary>
    /// The identity of a repository is the name of its directory under the runtime root, so it
    /// is a wire format that shipped: change what this produces and every machine's index
    /// becomes unreachable at once, presenting as every repository having lost its index.
    ///
    /// The expected value is not what the code returns today — it is read off a real runtime
    /// directory built by an earlier release, which is the only thing that can prove the two
    /// still agree.
    /// </summary>
    [Fact]
    public void The_identity_of_a_known_repository_is_the_directory_an_earlier_release_built()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The recorded directory was built by a Windows installation.");

        var identity = RepositoryIdentity.FromCommonDirectory(FsPath.From(@"R:\LocalAi\.git"));

        Assert.Equal(
            "0ecc90199fac80e34b0ad8dfe9daa8bffd7f6f2f5483b82297e7966ae1ec2ae3",
            identity.Id);
        Assert.Equal(@"R:\LOCALAI\.GIT", identity.CommonDirectory);
    }

    /// <summary>
    /// Whichever way the path was spelled by whoever asked. Before FsPath each caller normalised
    /// for itself, so this was a property of the callers rather than of the identity.
    /// </summary>
    [Theory]
    [InlineData(@"R:\LocalAi\.git")]
    [InlineData("R:/LocalAi/.git")]
    [InlineData(@"R:\LocalAi\.git\")]
    [InlineData(@"r:\localai\.GIT")]
    [InlineData(@"R:\LocalAi\src\..\.git")]
    public void Every_spelling_of_one_common_directory_gives_one_identity(string spelling)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters and case folding are Windows path rules.");

        Assert.Equal(
            "0ecc90199fac80e34b0ad8dfe9daa8bffd7f6f2f5483b82297e7966ae1ec2ae3",
            RepositoryIdentity.FromCommonDirectory(spelling).Id);
    }
}
