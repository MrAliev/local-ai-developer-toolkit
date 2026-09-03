using LocalAi.Cli;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

/// <summary>
/// Which strings `localai hook` will act on.
///
/// It is invoked by Git, unattended, and what it does is a full synchronisation including the
/// retention sweep — which deletes. So the question is not cosmetic: a string this accepts is a
/// string that can remove files with nobody watching, and exit 0 so that nothing downstream
/// notices.
///
/// The list that decides is the list of hooks actually installed. Anything else — an enum value
/// added ahead of its dispatcher, a numeral, a typo in a hook script — is a refusal.
/// </summary>
public sealed class HookEventDispatchTests
{
    [Theory]
    [InlineData("post-commit")]
    [InlineData("post-merge")]
    [InlineData("post-rewrite")]
    [InlineData("post-checkout")]
    public void Every_installed_hook_is_dispatched(string requested) =>
        Assert.True(
            HookCommand.IsDispatchedEvent(requested),
            $"{requested} is installed by GitHookLayout and must be answered");

    /// <summary>
    /// `ReferenceTransaction` was declared in the enum, installed by nothing, dispatched by
    /// nothing, and named in no message — and `localai hook reference-transaction` ran a full
    /// sweep and freed 7.4 MB on the machine this was found on.
    /// </summary>
    [Fact]
    public void An_event_no_hook_installs_is_refused() =>
        Assert.False(HookCommand.IsDispatchedEvent("reference-transaction"));

    /// <summary>
    /// `Enum.TryParse` accepts numerals, so `localai hook 3` parsed to the fourth event and swept.
    /// Nothing about a bare number is a Git hook name.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("99")]
    public void A_bare_number_is_not_an_event(string requested) =>
        Assert.False(HookCommand.IsDispatchedEvent(requested));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("post_commit")]
    [InlineData("postcommit")]
    [InlineData("pre-commit")]
    public void Anything_else_is_refused(string requested) =>
        Assert.False(HookCommand.IsDispatchedEvent(requested));

    /// <summary>
    /// Git spells its hooks in lower case, and so does the dispatcher this installs. Case is
    /// accepted because a hook script somebody wrote by hand may not be careful, and the answer
    /// is the same work either way.
    /// </summary>
    [Fact]
    public void Case_does_not_decide_it() =>
        Assert.True(HookCommand.IsDispatchedEvent("Post-Commit"));

    /// <summary>
    /// The guard and the message it prints have to rest on one list, or they drift apart — which
    /// is exactly what happened: the enum grew a fifth value and the message kept naming four.
    /// </summary>
    [Fact]
    public void The_message_names_exactly_what_is_dispatched()
    {
        foreach (var announced in CliUsage.HookEvents.Split('|'))
        {
            Assert.True(
                HookCommand.IsDispatchedEvent(announced),
                $"the usage line announces {announced}, which is not dispatched");
        }

        Assert.Equal(
            GitHookLayout.Events.Count,
            CliUsage.HookEvents.Split('|').Length);
    }
}
