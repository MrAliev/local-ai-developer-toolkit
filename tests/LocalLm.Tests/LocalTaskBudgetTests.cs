using System.Buffers.Binary;
using System.Text;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

/// <summary>
/// The aggregate budgets of #206: per-item limits alone never bounded a call, so many
/// individually acceptable files or images were all materialized before any limit was
/// consulted. Counts and totals must refuse before the first byte is read, and text must
/// stream into the shared budget rather than concatenate first.
/// </summary>
public sealed class LocalTaskBudgetTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(),
        "localai-budget-" + Guid.NewGuid().ToString("N"));

    public LocalTaskBudgetTests()
    {
        Directory.CreateDirectory(_work);
    }

    [Fact]
    public async Task More_files_than_the_limit_are_refused_before_anything_is_read()
    {
        var tasks = new LocalTasks(new CapturingClient());
        var files = Enumerable.Range(0, 65)
            .Select(index => Path.Combine(_work, $"missing-{index}.txt"))
            .ToArray();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => tasks.AskAsync(
                "summarise",
                files,
                model: null,
                TestContext.Current.CancellationToken));

        Assert.Contains("64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_past_the_shared_budget_is_truncated_not_materialized()
    {
        var path = Path.Combine(_work, "huge.txt");
        await File.WriteAllTextAsync(
            path,
            new string('A', 720_000) + new string('B', 1_000));
        var client = new CapturingClient();

        var result = await new LocalTasks(client).AskAsync(
            "summarise",
            [path],
            model: null,
            TestContext.Current.CancellationToken);

        Assert.Contains("TRUNCATED", client.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("BBBB", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("усечён", result.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_beyond_an_exhausted_budget_are_omitted_and_counted()
    {
        var first = Path.Combine(_work, "first.txt");
        var second = Path.Combine(_work, "second.txt");
        await File.WriteAllTextAsync(first, new string('A', 720_100));
        await File.WriteAllTextAsync(second, "SECOND-FILE-CONTENT");
        var client = new CapturingClient();

        var result = await new LocalTasks(client).AskAsync(
            "summarise",
            [first, second],
            model: null,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            "SECOND-FILE-CONTENT",
            client.LastPrompt,
            StringComparison.Ordinal);
        Assert.Contains("файлов пропущено: 1", result.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_images_than_the_limit_are_refused_before_anything_is_read()
    {
        var tasks = new LocalTasks(new CapturingClient());
        var paths = Enumerable.Range(0, 9)
            .Select(index => Path.Combine(_work, $"missing-{index}.png"))
            .ToArray();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => tasks.ReadImageAsync(
                paths,
                "describe",
                model: null,
                TestContext.Current.CancellationToken));

        Assert.Contains("8-image", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A one-kilobyte file whose header claims 30000x30000: the pixel total must refuse at
    /// the metadata pass, before a single image is base64-materialized.
    /// </summary>
    [Fact]
    public async Task A_pixel_total_past_the_budget_refuses_at_the_metadata_pass()
    {
        var path = Path.Combine(_work, "vast.png");
        await File.WriteAllBytesAsync(path, CraftPngHeader(30_000, 30_000));
        var tasks = new LocalTasks(new CapturingClient());

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => tasks.ReadImageAsync(
                [path],
                "describe",
                model: null,
                TestContext.Current.CancellationToken));

        Assert.Contains("pixel", error.Message, StringComparison.Ordinal);
    }

    private static byte[] CraftPngHeader(int width, int height)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    private sealed class CapturingClient : ILocalModelClient
    {
        public string LastPrompt { get; private set; } = string.Empty;

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
            CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(new LocalJobResult<string>(
                "answer",
                new LocalUsageReceipt(
                    Guid.NewGuid(),
                    "local-lm",
                    "chat",
                    "qwen3.5:9b",
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    prompt.Length,
                    prompt.Length / 4,
                    null,
                    null,
                    null,
                    new LocalRoutingReceipt(
                        profile,
                        "qwen3.5:9b",
                        requestedContextTokens,
                        WasCold: false,
                        UsedFallback: false,
                        ValidatorResult: "none:pass",
                        EstimatedGrossCloudTokensSaved: prompt.Length / 4,
                        EstimatedVerificationTokens: 0,
                        EstimatedNetCloudTokensSaved: prompt.Length / 4,
                        IsExperimentalAttempt: false))));
        }

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

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
