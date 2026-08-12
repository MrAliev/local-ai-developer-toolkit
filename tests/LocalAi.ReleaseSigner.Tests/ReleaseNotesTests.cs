using LocalAi.ReleaseSigner;

namespace LocalAi.ReleaseSigner.Tests;

public sealed class ReleaseNotesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-notes-" + Guid.NewGuid().ToString("N"));

    private static readonly ReleaseVersion Version = ReleaseVersion.Parse("0.1.36");

    [Fact]
    public void A_scaffold_is_not_mistaken_for_written_notes()
    {
        ReleaseNotes.Scaffold(_root, Version);

        var state = ReleaseNotes.Inspect(_root, Version);

        Assert.True(state.EnglishExists);
        Assert.True(state.RussianExists);
        Assert.True(state.StillTemplate);
        Assert.False(state.ReadyToPublish);
    }

    /// <summary>
    /// A release being retried must not lose the prose written for the first attempt.
    /// </summary>
    [Fact]
    public void Scaffolding_twice_leaves_written_notes_alone()
    {
        ReleaseNotes.Scaffold(_root, Version);
        var path = ReleaseNotes.EnglishPath(_root, Version);
        File.WriteAllText(path, $"# LocalAi {Version}\n\n[Русская версия]({Version}.ru.md)\n\nReal.\n");

        var created = ReleaseNotes.Scaffold(_root, Version);

        Assert.Empty(created);
        Assert.Contains("Real.", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// The heading is what the release page publishes, and one copied from an earlier release is
    /// invisible in review because every line of a new notes file is an added line.
    /// </summary>
    [Fact]
    public void A_heading_copied_from_an_earlier_release_is_caught()
    {
        Write($"# LocalAi 0.1.35\n\n[Русская версия]({Version}.ru.md)\n\nReal.\n", Russian());

        var state = ReleaseNotes.Inspect(_root, Version);

        Assert.False(state.ReadyToPublish);
        Assert.Contains(
            state.Problems,
            problem => problem.Contains("# LocalAi 0.1.35", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_link_to_the_Russian_notes_is_caught()
    {
        Write($"# LocalAi {Version}\n\nReal.\n", Russian());

        var state = ReleaseNotes.Inspect(_root, Version);

        Assert.False(state.ReadyToPublish);
        Assert.Contains(
            state.Problems,
            problem => problem.Contains("Russian counterpart", StringComparison.Ordinal));
    }

    [Fact]
    public void Notes_written_in_both_languages_are_ready()
    {
        Write($"# LocalAi {Version}\n\n[Русская версия]({Version}.ru.md)\n\nReal.\n", Russian());

        Assert.True(ReleaseNotes.Inspect(_root, Version).ReadyToPublish);
    }

    [Fact]
    public void Notes_missing_the_Russian_half_are_not_ready()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "releases"));
        File.WriteAllText(
            ReleaseNotes.EnglishPath(_root, Version),
            $"# LocalAi {Version}\n\n[Русская версия]({Version}.ru.md)\n\nReal.\n");

        var state = ReleaseNotes.Inspect(_root, Version);

        Assert.True(state.EnglishExists);
        Assert.False(state.RussianExists);
        Assert.False(state.ReadyToPublish);
    }

    private static string Russian() =>
        $"# LocalAi {Version}\n\n[English version]({Version}.md)\n\nНастоящее.\n";

    private void Write(string english, string russian)
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "releases"));
        File.WriteAllText(ReleaseNotes.EnglishPath(_root, Version), english);
        File.WriteAllText(ReleaseNotes.RussianPath(_root, Version), russian);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.1.36", "0.1.35")]
    [InlineData("0.2.0", "0.1.99")]
    [InlineData("1.0.0", "0.99.99")]
    [InlineData("0.1.36", "0.1.36-rc.1")]
    public void Ordering_puts_the_later_release_ahead(string later, string earlier)
    {
        Assert.True(ReleaseVersion.Parse(later).CompareTo(ReleaseVersion.Parse(earlier)) > 0);
    }

    /// <summary>
    /// The repository still carries v0.1.0 and v0.1.1 from a scheme this one replaced. Reading
    /// them as release versions would make the newest published version wrong in whichever
    /// direction the leading v happened to sort.
    /// </summary>
    [Fact]
    public void Tags_from_a_previous_scheme_are_not_reinterpreted()
    {
        // v9.9.9 is not a tag this repository has. It is here because the real ones — v0.1.0 and
        // v0.1.1 — sort below every current release, so reinterpreting them would change nothing
        // and the claim would hold by accident rather than by the rule.
        var newest = ReleaseVersion.Newest(
            ["v0.1.0", "v0.1.1", "v9.9.9", "0.1.34", "0.1.35", "not-a-tag"]);

        Assert.Equal("0.1.35", newest?.ToString());
    }

    [Fact]
    public void A_repository_with_no_releases_has_no_newest_version()
    {
        Assert.Null(ReleaseVersion.Newest(["v0.1.0", "nightly"]));
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("0.1.36.1")]
    [InlineData("v0.1.36")]
    [InlineData("")]
    public void Anything_that_is_not_a_release_version_is_rejected(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => ReleaseVersion.Parse(value));
    }
}
