using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Mcp;

namespace LocalLm.Tests;

/// <summary>
/// A refusal has to name the parameter the caller actually passed.
///
/// Every tool parsed its profile through one helper that named the parameter <c>taskProfile</c>,
/// which is right for three of the four tools and wrong for <c>read_image</c>, whose parameter is
/// <c>mode</c>. An agent handed "Unknown taskProfile 'Vision'." has been told to fix a parameter
/// that does not exist on the tool it called, and the obvious next move — passing
/// <c>taskProfile</c> — fails differently.
/// </summary>
public sealed class BadArgumentsNameTheirOwnParameterTests
{
    [Fact]
    public async Task Read_image_names_mode_rather_than_the_parameter_another_tool_has()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => LocalLmTools.ReadImage(
                Tasks(),
                ["C:\\nowhere\\absent.png"],
                "what is this",
                mode: "Vision",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("mode", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("taskProfile", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three tools the old name was right for keep it, and this is what says so rather
    /// than the fix quietly renaming everything to the newest caller.
    /// </summary>
    [Fact]
    public async Task Ask_local_still_names_its_own_parameter()
    {
        var answer = await LocalLmTools.AskLocal(
            Tasks(),
            "what is this",
            [],
            taskProfile: "Nonsense",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("taskProfile", answer, StringComparison.Ordinal);
    }

    private static LocalTasks Tasks() => new(new UnreachableClient());

    /// <summary>
    /// The argument never reaches a model: both calls are refused while reading the request,
    /// which is the point — a bad parameter must not cost a model load to report.
    /// </summary>
    private sealed class UnreachableClient : ILocalModelClient
    {
        public Task<LocalJobResult<string>> ChatAsync(
            string model,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) => throw Unreachable();

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
            CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile,
            string model,
            CancellationToken cancellationToken = default) => throw Unreachable();

        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default) => throw Unreachable();

        private static InvalidOperationException Unreachable() =>
            new("The request should have been refused before reaching a model.");
    }
}
