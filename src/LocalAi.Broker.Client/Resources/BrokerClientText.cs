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

    /// <summary>
    /// The same failure, with what was reported alongside it. A separate key rather than an
    /// empty hole: a job that recorded nothing would otherwise end in a dangling marker.
    /// </summary>
    public static string BrokerJobFailedWithReason(Guid jobId, string failureCode, string reason) =>
        Catalogue.Format(nameof(BrokerJobFailedWithReason), jobId, failureCode, reason);
}
