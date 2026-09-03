using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// What `localai sync` says has two audiences that must not be served the same way.
///
/// A person reads the prose, and it follows them into their own language. Another process reads
/// the `SYNCED` and `REFUSED` lines — `index_refresh` parses the second out of stdout — and those
/// must not move for anybody. The two live in one command's output, which is why they are asserted
/// together rather than in separate files: the day somebody translates the wrong one, this is the
/// file that says so.
/// </summary>
public sealed class SyncOutputFollowsTheReaderTests
{
    private const string RepositoryId =
        "0ecc90199fac80e34b0ad8dfe9daa8bffd7f6f2f5483b82297e7966ae1ec2ae3";

    /// <summary>
    /// Deliberately not an assertion about particular words. Pinning the sentence would make this
    /// a copy of the resource file; that the two languages differ at all is the behaviour.
    /// </summary>
    [Fact]
    public void The_busy_message_is_not_the_same_text_in_both_languages()
    {
        var english = new RepositorySyncBusyException(RepositoryId).Message;

        using var reading = TestCulture.Reading("ru");
        var russian = new RepositorySyncBusyException(RepositoryId).Message;

        Assert.NotEqual(english, russian, StringComparer.Ordinal);
    }

    /// <summary>
    /// The reader of this message is at a terminal or inside a Git hook, and the repository is
    /// named by a hash rather than by a path, so there is nothing to type it into. What it must
    /// carry is the identifier, whatever the language.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_busy_message_names_the_repository_in_every_language(string language)
    {
        using var reading = TestCulture.Reading(language);

        var message = new RepositorySyncBusyException(RepositoryId).Message;

        Assert.Contains(RepositoryId, message, StringComparison.Ordinal);
        Assert.Contains("Repository:", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal line is a wire format. `index_refresh` finds it by `REFUSED ` and reads the
    /// count after `files=`; a translated token would leave it reading every refusal as an
    /// ordinary result, which is the failure the constants were introduced to prevent.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_refusal_line_is_the_same_bytes_for_every_reader(string language)
    {
        using var reading = TestCulture.Reading(language);

        var line = SyncRefusal.Line(RepositoryId, files: 1200, limit: 400);

        Assert.Equal(
            $"REFUSED repository={RepositoryId} files=1200 limit=400 overlays=0",
            line,
            StringComparer.Ordinal);
        Assert.Equal(1200, SyncRefusal.Files(line));
        Assert.All(line, character => Assert.InRange(character, (char)32, (char)126));
    }
}
