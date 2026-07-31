using System.Security.Cryptography;
using System.Text;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalLm.Core;

public sealed class BrokerLocalModelClient(IBrokerClient broker) : ILocalModelClient
{
    public async Task<LocalJobResult<string>> ChatAsync(
        string model,
        string prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64,
        LocalJobPriority priority,
        CancellationToken cancellationToken = default)
    {
        var request = LocalJobRequestFactory.CreateChat(
            DeduplicationKey(model, prompt, system, imagesBase64),
            priority,
            model,
            prompt,
            system,
            imagesBase64);
        var result = await broker.ExecuteAsync<ChatJobOutput>(
            request,
            cancellationToken);
        return new LocalJobResult<string>(result.Value.Content, result.Receipt);
    }

    public async Task<LocalJobResult<string>> RoutedChatAsync(
        LocalTaskProfile profile,
        string prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64,
        LocalWorkloadMetadata workload,
        LocalWorkflowHint? workflow,
        string? modelOverride,
        int? requestedContextTokens,
        LocalJobPriority priority,
        CancellationToken cancellationToken = default)
    {
        var request = LocalJobRequestFactory.CreateRoutedChat(
            DeduplicationKey(
                modelOverride ?? profile.ToString(),
                prompt,
                system,
                imagesBase64),
            priority,
            profile,
            prompt,
            system,
            imagesBase64,
            workload,
            workflow,
            requestedContextTokens);
        if (modelOverride is not null)
        {
            request = LocalJobRequestFactory.ResolveRoutedChat(request, modelOverride);
        }

        var result = await broker.ExecuteAsync<ChatJobOutput>(
            request,
            cancellationToken);
        return new LocalJobResult<string>(result.Value.Content, result.Receipt);
    }

    public async Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var request = LocalJobRequestFactory.CreateListModels(
            "local-lm:list-models",
            LocalJobPriority.Interactive);
        var result = await broker.ExecuteAsync<ListModelsJobOutput>(
            request,
            cancellationToken);
        return new LocalJobResult<IReadOnlyList<string>>(
            result.Value.Models,
            result.Receipt);
    }

    public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
        CancellationToken cancellationToken = default) =>
        broker.ExecuteAsync<LocalModelsStatusOutput>(
            LocalJobRequestFactory.CreateModelControl(
                "local-lm:model-control:status",
                LocalJobPriority.Interactive,
                ModelControlOperation.Status),
            cancellationToken);

    public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
        string model,
        int contextTokens,
        string catalogVersion,
        CancellationToken cancellationToken = default) =>
        broker.ExecuteAsync<LocalModelPreflightOutput>(
            LocalJobRequestFactory.CreateModelPreflight(
                $"local-lm:model-control:preflight:{catalogVersion}:{model}:{contextTokens}",
                LocalJobPriority.Interactive,
                model,
                contextTokens,
                catalogVersion),
            cancellationToken);

    public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
        string model,
        string catalogVersion,
        CancellationToken cancellationToken = default) =>
        broker.ExecuteAsync<ModelMaintenanceJobOutput>(
            LocalJobRequestFactory.CreateModelMaintenance(
                $"local-lm:model-pull:{catalogVersion}:{model}",
                LocalJobPriority.Background,
                ModelMaintenanceOperation.Pull,
                model,
                catalogVersion),
            cancellationToken);

    public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
        LocalTaskProfile profile,
        string model,
        CancellationToken cancellationToken = default) =>
        broker.ExecuteAsync<LocalExperimentReportOutput>(
            LocalJobRequestFactory.CreateModelControl(
                $"local-lm:model-control:report:{profile}:{model}",
                LocalJobPriority.Interactive,
                ModelControlOperation.ExperimentReport,
                profile,
                model),
            cancellationToken);

    public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
        LocalTaskProfile profile,
        string model,
        ExperimentOwnerAction action,
        CancellationToken cancellationToken = default) =>
        broker.ExecuteAsync<LocalModelFeedbackOutput>(
            LocalJobRequestFactory.CreateModelControl(
                $"local-lm:model-control:feedback:{profile}:{model}:{action}",
                LocalJobPriority.Interactive,
                ModelControlOperation.Feedback,
                profile,
                model,
                action),
            cancellationToken);

    public Task<LocalJobResult<LocalExperimentCompletionOutput>>
        CompleteExperimentAsync(
            Guid workflowId,
            LocalTaskProfile profile,
            string model,
            ModelExecutionOutcome outcome,
            LocalExperimentTaskMetrics metrics,
            CancellationToken cancellationToken = default) =>
        broker.ExecuteAsync<LocalExperimentCompletionOutput>(
            LocalJobRequestFactory.CreateExperimentCompletion(
                $"local-lm:experiment-completion:{workflowId:N}:{profile}:{model}",
                LocalJobPriority.Foreground,
                workflowId,
                profile,
                model,
                outcome,
                metrics),
            cancellationToken);

    private static string DeduplicationKey(
        string model,
        string prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, model);
        Append(hash, prompt);
        Append(hash, system ?? string.Empty);
        foreach (var image in imagesBase64 ?? [])
        {
            Append(hash, image);
        }

        return "local-lm:chat:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}
