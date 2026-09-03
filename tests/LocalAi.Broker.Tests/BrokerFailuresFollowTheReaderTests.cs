using LocalAi.Broker.Client;
using LocalAi.Broker.Client.Resources;
using LocalAi.Tests.Shared;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The broker's own refusal, in the reader's language.
///
/// This is the single English sentence a Russian reader was most likely to actually meet. Every
/// LocalLm tool ends its failure path in <c>Local model call failed: {message}</c>, and for every
/// failure code but the two the tools handle themselves, that message is this one — so a fully
/// translated tool answer still ended in an English clause.
/// </summary>
public sealed class BrokerFailuresFollowTheReaderTests
{
    [Fact]
    public void A_failed_job_says_so_in_English_by_default()
    {
        var failure = new BrokerJobFailedException(Guid.Empty, "NoModelInstalledException");

        Assert.Contains("Broker job", failure.Message, StringComparison.Ordinal);
        Assert.Contains("NoModelInstalledException", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_job_says_so_in_Russian_for_a_Russian_reader()
    {
        using var reading = TestCulture.Reading("ru");

        var failure = new BrokerJobFailedException(Guid.Empty, "NoModelInstalledException");

        Assert.Contains("Задание брокера", failure.Message, StringComparison.Ordinal);
        Assert.Contains("NoModelInstalledException", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every catalogue carries this check, because a language that exists in the neutral
    /// resource and not in the translated one is how a half-Russian answer ships: every line
    /// looks translated until the one that is not.
    /// </summary>
    [Fact]
    public void Every_language_carries_every_string()
    {
        var gaps = BrokerClientText.Catalogue.Gaps();

        Assert.True(gaps.Count == 0, string.Join(Environment.NewLine, gaps));
    }
}
