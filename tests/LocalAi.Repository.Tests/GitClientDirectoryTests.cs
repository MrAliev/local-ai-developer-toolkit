using LocalAi.Repository;

namespace LocalAi.Repository.Tests;

/// <summary>
/// What happens before git is even started.
/// </summary>
public sealed class GitClientDirectoryTests
{
    /// <summary>
    /// A working directory that does not exist makes <c>Process.Start</c> throw
    /// <c>Win32Exception</c>, which is not in the family the callers catch — so a mistyped
    /// <c>--root</c> reached the entry point's last resort and was reported as an unexpected
    /// failure at exit 70, with the operating system's own words for it. It is an ordinary
    /// wrong path, and the answer to it is the one every other wrong path gets.
    /// </summary>
    [Fact]
    public async Task A_directory_that_is_not_there_is_not_an_unexpected_failure()
    {
        var absent = Path.Combine(
            Path.GetTempPath(),
            "localai-absent-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => new GitClient().GetCommonDirectoryAsync(
                absent,
                TestContext.Current.CancellationToken));
    }
}
