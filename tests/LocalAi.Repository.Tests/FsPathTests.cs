using LocalAi.Contracts;

namespace LocalAi.Repository.Tests;

/// <summary>
/// The differences this type exists to erase, each of which has cost a real index.
/// </summary>
public sealed class FsPathTests
{
    [Fact]
    public void The_slashes_git_prints_and_the_slashes_dotnet_prints_are_one_path()
    {
        // The defect this type is for: git prints forward slashes on Windows, Inspect yields
        // backslashes, and the same worktree hashed to two keys depending on which it came from.
        var fromGit = FsPath.From("R:/LocalAi/.claude/worktrees/one");
        var fromDotnet = FsPath.From(@"R:\LocalAi\.claude\worktrees\one");

        Assert.Equal(fromGit, fromDotnet);
        Assert.Equal(fromGit.IdentityKey, fromDotnet.IdentityKey);
    }

    [Fact]
    public void A_trailing_separator_does_not_make_a_different_directory()
    {
        Assert.Equal(FsPath.From(@"R:\LocalAi"), FsPath.From(@"R:\LocalAi\"));
    }

    [Fact]
    public void Relative_segments_are_resolved_away()
    {
        Assert.Equal(
            FsPath.From(@"R:\LocalAi\.git"),
            FsPath.From(@"R:\LocalAi\src\..\.git"));
    }

    [Fact]
    public void Case_follows_the_filesystem_rather_than_the_string()
    {
        var lower = FsPath.From(@"r:\localai");
        var upper = FsPath.From(@"R:\LOCALAI");

        Assert.Equal(OperatingSystem.IsWindows(), lower == upper);
        Assert.Equal(
            OperatingSystem.IsWindows(),
            lower.GetHashCode() == upper.GetHashCode());
    }

    /// <summary>
    /// Equality and hashing have to agree, or a set says it does not contain what it holds —
    /// which is exactly how a reachable-overlay lookup silently misses a live worktree.
    /// </summary>
    [Fact]
    public void A_set_finds_the_same_directory_spelled_another_way()
    {
        var set = new HashSet<FsPath> { FsPath.From(@"R:\LocalAi\src") };

        Assert.Contains(FsPath.From("R:/LocalAi/src/"), set);
    }

    [Fact]
    public void The_default_value_carries_no_path_and_says_so()
    {
        FsPath none = default;

        Assert.False(none.HasValue);
        Assert.Equal(string.Empty, none.ToString());
        Assert.Throws<InvalidOperationException>(() => none.Value);
    }

    [Fact]
    public void Nothing_is_not_a_path()
    {
        Assert.Throws<ArgumentException>(() => FsPath.From("   "));
        Assert.Null(FsPath.TryFrom(null));
        Assert.Null(FsPath.TryFrom(" "));
        Assert.NotNull(FsPath.TryFrom(@"R:\LocalAi"));
    }

    [Fact]
    public void Combining_keeps_the_result_canonical()
    {
        var combined = FsPath.From(@"R:\LocalAi").Combine("src", "LocalAi.Cli");

        Assert.Equal(FsPath.From("R:/LocalAi/src/LocalAi.Cli"), combined);
        Assert.Equal("LocalAi.Cli", combined.Name);
        Assert.Equal(FsPath.From(@"R:\LocalAi\src"), combined.Parent);
    }

    /// <summary>
    /// A trailing newline is what a git subprocess leaves behind; no directory is named for it.
    /// </summary>
    [Theory]
    [InlineData("R:/LocalAi\n")]
    [InlineData("R:/LocalAi\r\n")]
    [InlineData("R:/LocalAi  ")]
    public void Whatever_a_subprocess_left_on_the_end_is_not_part_of_the_path(string printed)
    {
        Assert.Equal(FsPath.From(@"R:\LocalAi"), FsPath.From(printed));
    }

    /// <summary>
    /// And the front is left alone, because a leading space is a legal directory name: trimming
    /// it would resolve to a different directory than the one asked for, which is the class of
    /// difference this type exists to remove rather than to create.
    /// </summary>
    [Fact]
    public void A_leading_space_is_part_of_the_name_and_is_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path rules.");

        Assert.Equal(@"R:\ leading", FsPath.From(@"R:\ leading").Value);
        Assert.NotEqual(FsPath.From(@"R:\leading"), FsPath.From(@"R:\ leading"));
    }

    /// <summary>
    /// Path.Combine lets a rooted segment discard everything to its left, so a path assembled
    /// from configuration can land somewhere nobody named. What comes back is under what it was
    /// called on, or nothing comes back.
    /// </summary>
    [Fact]
    public void Combining_cannot_leave_the_path_it_was_called_on()
    {
        var root = FsPath.From(@"R:\LocalAi");

        Assert.Throws<ArgumentException>(() => root.Combine(@"C:\Windows"));
        Assert.Throws<ArgumentException>(() => root.Combine("..", "elsewhere"));
        Assert.Equal(root, root.Combine("src", ".."));
    }

    /// <summary>
    /// A short (8.3) name is expanded for the parts that exist on disk. Worth pinning because it
    /// is not obvious from the API, and because the opposite belief — held while writing this
    /// type — would have put a false claim in its documentation.
    /// </summary>
    [Fact]
    public void A_short_name_of_a_directory_that_exists_is_the_same_path_as_its_long_form()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Short (8.3) names are a Windows filesystem feature.");

        Assert.Equal(FsPath.From(@"C:\Program Files"), FsPath.From(@"C:\PROGRA~1"));
    }

    /// <summary>
    /// And is not expanded for one that does not, so a canonical spelling is not the same
    /// promise as a canonical identity. Stated here so the limit is found in a test rather than
    /// by someone trusting the type to answer a question it cannot.
    /// </summary>
    [Fact]
    public void A_short_name_of_something_absent_stays_as_it_was_given()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Short (8.3) names are a Windows filesystem feature.");

        Assert.Equal(@"C:\NOSUCH~1", FsPath.From(@"C:\NOSUCH~1").Value);
    }
}
