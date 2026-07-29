using LocalAi.Contracts;

namespace LocalLm.Core;

public interface ILocalModelClient
{
    Task<LocalJobResult<string>> ChatAsync(
        string model,
        string prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64,
        LocalJobPriority priority,
        CancellationToken cancellationToken = default);

    Task<LocalJobResult<string>> RoutedChatAsync(
        LocalTaskProfile profile,
        string prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64,
        LocalWorkloadMetadata workload,
        LocalWorkflowHint? workflow,
        string? modelOverride,
        int? requestedContextTokens,
        LocalJobPriority priority,
        CancellationToken cancellationToken = default);

    Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
        CancellationToken cancellationToken = default);

    Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
        CancellationToken cancellationToken = default);

    Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
        string model,
        int contextTokens,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Model preflight is not supported by this client.");

    Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
        string model,
        string catalogVersion,
        CancellationToken cancellationToken = default);

    Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
        LocalTaskProfile profile,
        string model,
        CancellationToken cancellationToken = default);

    Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
        LocalTaskProfile profile,
        string model,
        ExperimentOwnerAction action,
        CancellationToken cancellationToken = default);

    Task<LocalJobResult<LocalExperimentCompletionOutput>> CompleteExperimentAsync(
        Guid workflowId,
        LocalTaskProfile profile,
        string model,
        ModelExecutionOutcome outcome,
        LocalExperimentTaskMetrics metrics,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Experiment completion is not supported by this client.");
}
