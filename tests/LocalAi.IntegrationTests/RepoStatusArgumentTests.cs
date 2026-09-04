using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// `repo status` used to read its first argument as a Git common directory whatever it was, so
/// `--root C:\repo` — the form every other command in this CLI takes — hashed the literal string
/// `--root` into a repository id nobody had configured, and answered NOT_CONFIGURED about a
/// repository that was configured. Silently, with exit code 0.
/// </summary>
public sealed class RepoStatusArgumentTests
{
    [Fact]
    public void No_arguments_means_the_current_directory_resolved_through_git()
    {
        Assert.True(RepoCommand.TryParseStatusArguments([], out var target, out var refusal));

        Assert.Null(refusal);
        Assert.Null(target.Path);
        Assert.True(target.ResolveThroughGit);
    }

    [Fact]
    public void A_root_directory_is_resolved_through_git()
    {
        Assert.True(RepoCommand.TryParseStatusArguments(
            ["--root", @"R:\Repo"],
            out var target,
            out var refusal));

        Assert.Null(refusal);
        Assert.Equal(@"R:\Repo", target.Path);
        Assert.True(target.ResolveThroughGit);
    }

    [Fact]
    public void A_bare_path_is_taken_as_the_common_directory_itself()
    {
        Assert.True(RepoCommand.TryParseStatusArguments(
            [@"R:\Repo\.git"],
            out var target,
            out var refusal));

        Assert.Null(refusal);
        Assert.Equal(@"R:\Repo\.git", target.Path);
        Assert.False(target.ResolveThroughGit);
    }

    [Theory]
    [InlineData("--rooot")]
    [InlineData("-r")]
    [InlineData("--whatever")]
    public void An_option_it_does_not_know_is_refused_rather_than_read_as_a_path(string argument)
    {
        Assert.False(RepoCommand.TryParseStatusArguments(
            [argument],
            out _,
            out var refusal));

        Assert.Contains(argument, refusal!.Message);
    }

    [Fact]
    public void A_root_without_a_directory_is_refused()
    {
        Assert.False(RepoCommand.TryParseStatusArguments(["--root"], out _, out var refusal));

        Assert.Contains("requires a directory", refusal!.Message);
    }

    [Fact]
    public void Two_root_directories_are_refused() =>
        AssertRefusesTwo(["--root", @"R:\One", "--root", @"R:\Two"]);

    [Fact]
    public void Two_bare_paths_are_refused() =>
        AssertRefusesTwo([@"R:\One\.git", @"R:\Two\.git"]);

    [Fact]
    public void A_bare_path_and_a_root_together_are_refused() =>
        AssertRefusesTwo([@"R:\One\.git", "--root", @"R:\Two"]);

    private static void AssertRefusesTwo(string[] arguments)
    {
        Assert.False(RepoCommand.TryParseStatusArguments(arguments, out _, out var refusal));

        Assert.Contains("one repository", refusal!.Message);
    }
}
