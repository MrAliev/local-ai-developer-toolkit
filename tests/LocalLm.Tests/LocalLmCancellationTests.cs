using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Mcp;

namespace LocalLm.Tests;

/// <summary>
/// #209/m3: a host cancellation used to come back as "Local model call failed: The
/// operation was canceled" — a model failure inviting a retry nobody is waiting for. The
/// wrapper now lets the caller's own cancellation propagate, while an
/// OperationCanceledException the caller did not ask for — the shape of an HTTP timeout —
/// still becomes readable text.
/// </summary>
public sealed class LocalLmCancellationTests
{
    [Fact]
    public async Task The_hosts_cancellation_propagates_instead_of_becoming_a_tool_error()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalLmTools.AskLocal(
            new LocalTasks(new RoutedClient(token =>
            {
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("should not be reached");
            })),
            "list the TODOs",
            files: null,
            taskProfile: "ShortSummary",
            model: null,
            cancelled.Token));
    }

    [Fact]
    public async Task Translate_cancellation_propagates_too()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalLmTools.TranslateLocal(
                new LocalTasks(new RoutedClient(token =>
                {
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("should not be reached");
                })),
                "привет мир",
                "Russian",
                "English",
                markdown: false,
                cancelled.Token));
    }

    [Fact]
    public async Task A_timeout_the_caller_did_not_ask_for_stays_readable_text()
    {
        var response = await LocalLmTools.AskLocal(
            new LocalTasks(new RoutedClient(_ =>
                throw new TaskCanceledException("the HTTP request timed out"))),
            "list the TODOs",
            files: null,
            taskProfile: "ShortSummary",
            model: null,
            TestContext.Current.CancellationToken);

        Assert.StartsWith(
            "Local model call failed",
            response,
            StringComparison.Ordinal);
    }

    private sealed class RoutedClient(
        Func<CancellationToken, LocalJobResult<string>> routed) : ILocalModelClient
    {
        public Task<LocalJobResult<string>> ChatAsync(
            string model,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<string>> RoutedChatAsync(
            LocalTaskProfile profile,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalWorkloadMetadata workload,
            LocalWorkflowHint? workflow,
            string? modelOverride,
            int? requestedContextTokens,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(routed(cancellationToken));

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
