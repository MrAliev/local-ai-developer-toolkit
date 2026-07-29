using LocalAi.Contracts;

namespace LocalLm.Core;

public sealed record LocalModelSyncResult(
    string CatalogVersion,
    IReadOnlyList<string> InstalledModels);

public sealed class ModelManagementTasks(ILocalModelClient client)
{
    private readonly ILocalModelClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    public async Task<LocalModelsStatusOutput> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        (await _client.GetModelsStatusAsync(cancellationToken)).Value;

    public async Task<LocalModelPreflightOutput> PreflightAsync(
        string model,
        int contextTokens,
        CancellationToken cancellationToken = default) =>
        (await _client.PreflightModelAsync(
            model,
            contextTokens,
            cancellationToken)).Value;

    public async Task<LocalModelSyncResult> SyncRecommendedAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var installed = new List<string>(status.RecommendedMissingModels.Count);
        foreach (var model in status.RecommendedMissingModels)
        {
            await _client.PullModelAsync(
                model,
                status.CatalogVersion,
                cancellationToken);
            installed.Add(model);
        }

        return new LocalModelSyncResult(
            status.CatalogVersion,
            Array.AsReadOnly(installed.ToArray()));
    }

    public async Task<LocalExperimentReportOutput> GetExperimentReportAsync(
        LocalTaskProfile profile,
        string model,
        CancellationToken cancellationToken = default) =>
        (await _client.GetExperimentReportAsync(
            profile,
            model,
            cancellationToken)).Value;

    public async Task<LocalModelFeedbackOutput> ApplyFeedbackAsync(
        LocalTaskProfile profile,
        string model,
        ExperimentOwnerAction action,
        CancellationToken cancellationToken = default) =>
        (await _client.ApplyFeedbackAsync(
            profile,
            model,
            action,
            cancellationToken)).Value;
}
