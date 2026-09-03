using System.Text;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Consent to look up releases, and the record of what the last look-up found.
///
/// The rule these exist for is one-directional: every way of failing to read the policy has to
/// land on "off". A check is a request to GitHub, and the product's promise is that the
/// runtime makes none the user did not ask for — so a corrupt file, an unknown schema or an
/// unreadable directory must never be the reason a machine starts talking to the network.
/// </summary>
public sealed class UpdateCheckPolicyTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-update-policy-" + Guid.NewGuid().ToString("N"));

    public UpdateCheckPolicyTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_machine_nobody_configured_does_not_check()
    {
        var policy = new UpdateCheckPolicyStore(root).Read();

        Assert.False(policy.Enabled);
        Assert.Equal(UpdateCheckPolicy.DefaultIntervalHours, policy.IntervalHours);
        Assert.Equal(UpdateCheckPolicy.Default, policy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"SchemaVersion":99,"Enabled":true,"IntervalHours":24}""")]
    [InlineData("""{"SchemaVersion":1,"Enabled":true,"IntervalHours":0}""")]
    [InlineData("""{"SchemaVersion":1,"Enabled":true,"IntervalHours":100000}""")]
    public void Anything_unreadable_means_off_rather_than_on(string content)
    {
        File.WriteAllText(
            Path.Combine(root, UpdateCheckPolicy.FileName),
            content,
            Encoding.UTF8);

        var policy = new UpdateCheckPolicyStore(root).Read();

        Assert.False(policy.Enabled);
    }

    [Fact]
    public void What_was_agreed_to_survives_a_round_trip()
    {
        var store = new UpdateCheckPolicyStore(root);

        store.Write(UpdateCheckPolicy.Default with { Enabled = true, IntervalHours = 12 });

        var policy = store.Read();
        Assert.True(policy.Enabled);
        Assert.Equal(12, policy.IntervalHours);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(UpdateCheckPolicy.MaximumIntervalHours + 1)]
    public void An_interval_nobody_could_have_meant_is_refused_at_the_write(int hours)
    {
        var store = new UpdateCheckPolicyStore(root);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Write(UpdateCheckPolicy.Default with { IntervalHours = hours }));
    }

    /// <summary>
    /// The sentence the installer's checkbox and the CLI both print. Pinned by its claims
    /// rather than word for word: an opt-in that stops saying what it sends is the problem
    /// this feature was designed around.
    /// </summary>
    [Theory]
    [InlineData("Nothing about this machine is sent")]
    [InlineData("verifies the signature")]
    [InlineData("anonymous")]
    [InlineData("without you asking")]
    public void The_disclosure_says_what_is_sent_and_what_is_not(string claim) =>
        Assert.Contains(claim, UpdateCheckPolicy.Disclosure, StringComparison.Ordinal);

    /// <summary>
    /// The same four claims in the second language, pinned the same way.
    ///
    /// Until the CLI learned to pick, nothing read this constant except an installer window,
    /// and so nothing checked that it still promised what its English twin promises. Consent
    /// described differently in two languages is consent taken under two different terms.
    /// </summary>
    [Theory]
    [InlineData("Об этом компьютере ничего не отправляется")]
    [InlineData("проверяет подпись")]
    [InlineData("анонимным запросом")]
    [InlineData("без вашего запроса")]
    public void The_Russian_disclosure_makes_the_same_promises(string claim) =>
        Assert.Contains(claim, UpdateCheckPolicy.DisclosureRussian, StringComparison.Ordinal);
}
