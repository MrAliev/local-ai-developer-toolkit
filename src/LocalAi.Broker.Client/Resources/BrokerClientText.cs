using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Client.Resources;

/// <summary>
/// What the broker client says when a job does not come back.
///
/// One sentence, and the one a Russian reader was most likely to actually meet: every LocalLm
/// tool ends its failure path in "Local model call failed: {message}", and for every failure code
/// but the two the tools answer themselves, that message is this one. A fully translated answer
/// still finished in English.
/// </summary>
public static class BrokerClientText
{
    public static TextCatalogue Catalogue { get; } = new(
        "LocalAi.Broker.Client.Resources.BrokerClientText",
        typeof(BrokerClientText).Assembly);

    public static string BrokerJobFailed(Guid jobId, string failureCode) =>
        Catalogue.Format(nameof(BrokerJobFailed), jobId, failureCode);
}
