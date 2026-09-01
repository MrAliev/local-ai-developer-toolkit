using System.Text;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalLm.Core;

public sealed record LocalResult(
    string Answer,
    int SavedTokens,
    string Model,
    string Detail,
    LocalUsageReceipt Receipt)
{
    /// <summary>
    /// The line the caller is expected to surface, so what a local call cost and saved is
    /// visible rather than silent.
    ///
    /// The duration was measured all along — the broker records how long a job waited and how
    /// long it ran — and then went only into experiment telemetry, so a caller asked to report
    /// it could only guess. It comes from the receipt now.
    /// </summary>
    public string Notice =>
        $"🔧 Локально: {Model}. {Detail}. {DescribeDuration()}. " +
        TokenEstimator.DescribeSaving(SavedTokens);

    /// <summary>
    /// How long it took, and how much of that was waiting.
    ///
    /// The two are different stories: four seconds behind another client is a queue to look
    /// at, four seconds of inference is a model to look at. The wait is only named when it is
    /// a real share of the total, so the ordinary line stays short.
    /// </summary>
    private string DescribeDuration()
    {
        var total = Receipt.QueueDuration + Receipt.ExecutionDuration;
        var queued = Receipt.QueueDuration;
        return queued >= TimeSpan.FromSeconds(0.5) && queued >= total * 0.2
            ? $"{Seconds(total)} (в очереди {Seconds(queued)})"
            : Seconds(total);
    }

    private static string Seconds(TimeSpan span) =>
        span < TimeSpan.FromSeconds(10)
            ? $"{span.TotalSeconds:0.0} с"
            : $"{span.TotalSeconds:0} с";
}

/// <summary>
/// The delegated jobs themselves. Each one reports what it consumed locally so the caller can
/// state a measured saving rather than a guessed one.
/// </summary>
public sealed class LocalTasks
{
    private readonly ILocalModelClient client;
    private readonly LogTriagePolicyStore logTriagePolicies;

    public LocalTasks(ILocalModelClient client)
        : this(client, new LogTriagePolicyStore(LogTriagePolicyStore.DefaultRuntimeRoot))
    {
    }

    internal LocalTasks(
        ILocalModelClient client,
        LogTriagePolicyStore logTriagePolicies)
    {
        this.client = client;
        this.logTriagePolicies = logTriagePolicies;
    }

    private const long MaxImageBytes = 30 * 1024 * 1024;

    /// <summary>
    /// The aggregate bounds (#206). Per-item limits alone did not bound the call: many files
    /// or images, each individually acceptable, were all materialized before any limit was
    /// consulted, and one local MCP call could take the server down. The counts and totals
    /// are checked against metadata before anything is read, and text is streamed into the
    /// shared character budget rather than concatenated first.
    /// </summary>
    private const int MaxAskFiles = 64;

    private const int MaxImageCount = 8;

    private const long MaxTotalImageBytes = 60 * 1024 * 1024;

    private const long MaxTotalImagePixels = 80_000_000;

    /// <summary>
    /// Text sent to a local model in one call. Well inside these models' windows, and past this a
    /// single answer stops being trustworthy anyway.
    /// </summary>
    private const int MaxPromptChars = 720_000;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
    };

    public async Task<LocalResult> ReadImageAsync(
        IReadOnlyList<string> paths, string question, string? model, CancellationToken ct)
        => await ReadImageAsync(
            paths,
            question,
            LocalTaskProfile.VisualAnalysis,
            model,
            ct);

    public async Task<LocalResult> ReadImageAsync(
        IReadOnlyList<string> paths,
        string question,
        LocalTaskProfile profile,
        string? model,
        CancellationToken ct)
    {
        if (profile is not (
                LocalTaskProfile.VisualAnalysis or
                LocalTaskProfile.Ocr or
                LocalTaskProfile.ImageTranslation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                "Image tasks support VisualAnalysis, Ocr, or ImageTranslation.");
        }

        if (paths.Count == 0)
        {
            throw new ArgumentException("No image paths given.", nameof(paths));
        }

        if (paths.Count > MaxImageCount)
        {
            throw new ArgumentException(
                $"{paths.Count} images exceed the {MaxImageCount}-image limit for one call; " +
                "split the request.",
                nameof(paths));
        }

        var wouldHaveCost = 0;
        long totalImagePixels = 0;
        long totalImageBytes = 0;
        var described = new List<string>(paths.Count);
        var fullPaths = new List<string>(paths.Count);

        // Metadata first, bytes second (#206): every base64 string is held in memory at once,
        // so the totals have to refuse the batch before the first file is materialized.
        foreach (var path in paths)
        {
            var full = Resolve(path);
            if (!ImageExtensions.Contains(Path.GetExtension(full)))
            {
                throw new ArgumentException($"'{full}' does not look like an image.", nameof(paths));
            }

            var length = new FileInfo(full).Length;
            if (length > MaxImageBytes)
            {
                throw new ArgumentException($"'{full}' is {length / 1024 / 1024}MB, past the {MaxImageBytes / 1024 / 1024}MB limit.");
            }

            totalImageBytes = checked(totalImageBytes + length);
            if (totalImageBytes > MaxTotalImageBytes)
            {
                throw new ArgumentException(
                    $"The images together exceed the {MaxTotalImageBytes / 1024 / 1024}MB " +
                    "total limit for one call; split the request.",
                    nameof(paths));
            }

            var info = ImageInfo.Read(full);
            wouldHaveCost += TokenEstimator.ForImage(info);
            totalImagePixels = checked(
                totalImagePixels + (long)info.Width * info.Height);
            if (totalImagePixels > MaxTotalImagePixels)
            {
                throw new ArgumentException(
                    $"The images together exceed the {MaxTotalImagePixels:N0}-pixel " +
                    "total limit for one call; split the request.",
                    nameof(paths));
            }

            described.Add($"{Path.GetFileName(full)} ({info.Width}x{info.Height})");
            fullPaths.Add(full);
        }

        var images = new List<string>(fullPaths.Count);
        foreach (var full in fullPaths)
        {
            images.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(full, ct)));
        }

        const string imageReadingSystem =
            "You are reading images on behalf of another engineer who will never see them. " +
            "Answer only from what is actually visible. Transcribe text exactly, preserving " +
            "the original language. If something is unreadable, say so explicitly instead " +
            "of guessing.";
        const string imageTranslationSystem =
            "Translate all visible natural-language text faithfully. Preserve layout order, " +
            "numbers, identifiers, URLs, and unreadable markers. Return only the translated " +
            "content and never invent text that is not visible.";
        var imageSystem = profile == LocalTaskProfile.ImageTranslation
            ? imageTranslationSystem
            : imageReadingSystem;
        var workflowId = profile == LocalTaskProfile.ImageTranslation
            ? Guid.NewGuid()
            : (Guid?)null;
        var result = await client.RoutedChatAsync(
            profile,
            question,
            imageSystem,
            images,
            new LocalWorkloadMetadata(
                question.Length,
                4_000,
                0,
                images.Count,
                totalImagePixels,
                LocalDurationClass.Medium),
            workflowId is { } imageWorkflowId
                ? new LocalWorkflowHint(
                    imageWorkflowId,
                    stepIndex: 0,
                    expectedStepCount: 1,
                    [profile],
                    isDependencyReady: true)
                : null,
            modelOverride: model,
            requestedContextTokens: 8192,
            LocalJobPriority.Foreground,
            ct);
        var chosen = result.Receipt.Routing?.SelectedModel ?? result.Receipt.Model;
        var routing = result.Receipt.Routing;
        var experimentalModel = routing?.ExperimentalModel ??
                                (routing?.IsExperimentalAttempt == true
                                    ? routing.SelectedModel
                                    : null);
        if (workflowId is { } completedWorkflowId &&
            experimentalModel is not null)
        {
            var inputTokens = checked(
                wouldHaveCost +
                TokenEstimator.ForText(question) +
                TokenEstimator.ForText(imageSystem));
            var outputTokens = TokenEstimator.ForText(result.Value);
            await client.CompleteExperimentAsync(
                completedWorkflowId,
                profile,
                experimentalModel,
                routing?.ExperimentalOutcome ??
                ModelExecutionOutcome.Success,
                new LocalExperimentTaskMetrics(
                    inputTokens,
                    outputTokens,
                    checked(inputTokens + outputTokens),
                    outputTokens,
                    Math.Max(0, wouldHaveCost - outputTokens),
                    result.Receipt.QueueDuration +
                    result.Receipt.ExecutionDuration,
                    ColdExecutions: routing?.WasCold == true ? 1 : 0,
                    WarmExecutions: routing?.WasCold == false ? 1 : 0,
                    UsedFallback: routing?.UsedFallback == true),
                ct);
        }

        return new LocalResult(
            result.Value,
            TokenEstimator.Saved(wouldHaveCost, result.Value),
            chosen,
            $"прочитано изображений: {images.Count} — {string.Join(", ", described)}",
            result.Receipt);
    }

    public Task<LocalResult> TriageLogAsync(
        string path, string? question, string? model, CancellationToken ct) =>
        TriageLogAsync(path, text: null, question, model, ct);

    public Task<LocalResult> TriageLogAsync(
        string? path,
        string? text,
        string? question,
        string? model,
        CancellationToken ct) =>
        new LogTriagePipeline(client, logTriagePolicies.Read())
            .RunAsync(path, text, question, model, ct);

    public async Task<LocalResult> AskAsync(
        string prompt, IReadOnlyList<string> files, string? model, CancellationToken ct)
        => await AskAsync(
            LocalTaskProfile.ShortSummary,
            prompt,
            files,
            model,
            ct);

    public async Task<LocalResult> AskAsync(
        LocalTaskProfile profile,
        string prompt,
        IReadOnlyList<string> files,
        string? model,
        CancellationToken ct)
    {
        if (profile is (
                LocalTaskProfile.PlainTranslation or
                LocalTaskProfile.TechnicalTranslation or
                LocalTaskProfile.ExactSearch or
                LocalTaskProfile.VectorEmbedding or
                LocalTaskProfile.Ocr or
                LocalTaskProfile.VisualAnalysis or
                LocalTaskProfile.ImageTranslation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                $"Task profile '{profile}' is not a text-chat profile.");
        }

        if (files.Count > MaxAskFiles)
        {
            throw new ArgumentException(
                $"{files.Count} files exceed the {MaxAskFiles}-file limit for one call; " +
                "split the request.",
                nameof(files));
        }

        var bundle = new StringBuilder();
        var wouldHaveCost = 0;
        var names = new List<string>();
        var remaining = MaxPromptChars;
        var omitted = 0;

        foreach (var path in files)
        {
            var full = Resolve(path);
            names.Add(Path.GetFileName(full));
            var header = $"--- FILE: {full} ---";
            if (remaining <= header.Length + 2)
            {
                omitted++;
                continue;
            }

            bundle.AppendLine(header);
            remaining -= header.Length + 2;
            // Streamed into the shared budget, never materialized whole first: reading every
            // file and clamping the concatenation afterwards meant the limit bounded the
            // prompt but not the peak memory (#206).
            var (content, truncated) = await ReadBoundedTextAsync(full, remaining, ct);
            wouldHaveCost += TokenEstimator.ForText(content);
            remaining -= content.Length;
            bundle.AppendLine(content);
            if (truncated)
            {
                bundle.AppendLine("--- TRUNCATED: the shared input budget is exhausted ---");
                remaining = 0;
            }

            bundle.AppendLine();
            remaining = Math.Max(0, remaining - 2);
        }

        var body = Clamp(bundle.ToString());
        var boundedNote = omitted > 0 || remaining == 0
            ? $"; ввод усечён общим бюджетом {MaxPromptChars} символов" +
              (omitted > 0 ? $", файлов пропущено: {omitted}" : string.Empty)
            : string.Empty;

        var requestBody = files.Count == 0 ? prompt : $"{prompt}\n\n{body}";
        var result = await client.RoutedChatAsync(
            profile,
            requestBody,
            "You are doing mechanical work for an engineer who will not read these files themselves. " +
            "Answer strictly from the supplied content, quote exact identifiers and line content, and " +
            "say explicitly when the files do not contain the answer rather than filling the gap.",
            null,
            new LocalWorkloadMetadata(
                requestBody.Length,
                4_000,
                files.Count,
                0,
                0,
                files.Count > 4 ? LocalDurationClass.Medium : LocalDurationClass.Short),
            workflow: null,
            modelOverride: model,
            requestedContextTokens: SelectContext(requestBody.Length),
            LocalJobPriority.Foreground,
            ct);
        var chosen = result.Receipt.Routing?.SelectedModel ?? result.Receipt.Model;

        var detail = (files.Count == 0
            ? "выполнен запрос без файлов"
            : $"обработано файлов: {files.Count} — {string.Join(", ", names.Take(5))}{(names.Count > 5 ? ", …" : string.Empty)}") +
            boundedNote;

        return new LocalResult(
            result.Value,
            TokenEstimator.Saved(wouldHaveCost, result.Value),
            chosen,
            detail,
            result.Receipt);
    }

    public async Task<LocalTranslationResult> TranslateAsync(
        string source,
        string sourceLanguage,
        string targetLanguage,
        bool markdown,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        var profile = markdown
            ? LocalTaskProfile.TechnicalTranslation
            : LocalTaskProfile.PlainTranslation;
        var parts = markdown
            ? TranslationProtector.SplitFencedCode(source)
            : [new TranslationPart(source, IsTranslatable: true)];
        var work = parts
            .SelectMany(part =>
                part.IsTranslatable && !string.IsNullOrWhiteSpace(part.Text)
                ? TranslationChunker.Chunk(part.Text)
                    .Select(chunk => new TranslationWorkItem(
                        chunk.Text,
                        IsTranslatable: true))
                : [new TranslationWorkItem(
                    part.Text,
                    IsTranslatable: false)])
            .ToArray();
        var modelStepCount = work.Count(item => item.IsTranslatable);
        var profiles = Enumerable.Repeat(profile, modelStepCount).ToArray();
        var workflowId = Guid.NewGuid();
        async Task<TranslationAttempt> RunAttemptAsync(string? modelOverride)
        {
            var translated = new StringBuilder();
            var receipts = new List<LocalUsageReceipt>();
            var inputTokens = 0;
            var outputTokens = 0;
            LocalUsageReceipt? lastReceipt = null;
            var modelStep = 0;
            foreach (var item in work)
            {
                if (!item.IsTranslatable)
                {
                    translated.Append(item.Text);
                    continue;
                }

                var (leadingLineBreaks, text, trailingLineBreaks) =
                    markdown
                        ? SplitBoundaryLineBreaks(item.Text)
                        : (string.Empty, item.Text, string.Empty);
                var prompt =
                    $"Translate the following fragment from {sourceLanguage} to {targetLanguage}. " +
                    "Return only the translated fragment. Preserve Markdown structure, code, URLs, " +
                    "placeholders, and line endings exactly where they are not natural language.\r\n\r\n" +
                    text;
                var result = await client.RoutedChatAsync(
                    profile,
                    prompt,
                    "Translate faithfully without explanations or omissions.",
                    null,
                    new LocalWorkloadMetadata(
                        text.Length,
                        text.Length,
                        0,
                        0,
                        0,
                        LocalDurationClass.Short),
                    new LocalWorkflowHint(
                        workflowId,
                        modelStep,
                        modelStepCount,
                        profiles,
                        isDependencyReady: true),
                    modelOverride,
                    requestedContextTokens: SelectContext(
                        checked(prompt.Length + (text.Length * 2))),
                    LocalJobPriority.Foreground,
                    ct);
                receipts.Add(result.Receipt);
                inputTokens = checked(inputTokens + TokenEstimator.ForText(prompt));
                outputTokens = checked(
                    outputTokens + TokenEstimator.ForText(result.Value));
                translated
                    .Append(leadingLineBreaks)
                    .Append(result.Value.Trim('\r', '\n'))
                    .Append(trailingLineBreaks);
                lastReceipt = result.Receipt;
                modelStep++;
            }

            return new TranslationAttempt(
                translated.ToString(),
                lastReceipt
                    ?? throw new InvalidOperationException(
                        "Translation produced no model receipt."),
                Array.AsReadOnly(receipts.ToArray()),
                inputTokens,
                outputTokens);
        }

        var experimentalAttempt = await RunAttemptAsync(modelOverride: null);
        var attempt = experimentalAttempt;
        var attempts = new List<TranslationAttempt> { experimentalAttempt };
        var validation = markdown
            ? TranslationValidator.ValidateMarkdown(source, attempt.Text)
            : TranslationValidator.ValidatePlain(source, attempt.Text);
        var brokerFailureOutcome = attempt.Receipts
            .Select(receipt => receipt.Routing?.ExperimentalOutcome)
            .FirstOrDefault(outcome => outcome is not null);
        var experimentalOutcome = brokerFailureOutcome ??
            (validation.Passed
                ? ModelExecutionOutcome.Success
                : ModelExecutionOutcome.StructuralFailure);
        var usedFallback = attempt.Receipts.Any(
            receipt => receipt.Routing?.UsedFallback == true);
        if (!validation.Passed)
        {
            usedFallback = true;
            var fallbackModels = markdown
                ? new[] { "qwen2.5-coder:14b", "qwen3.5:9b" }
                : ["qwen3.5:9b"];
            for (var index = 0; index < fallbackModels.Length; index++)
            {
                try
                {
                    attempt = await RunAttemptAsync(fallbackModels[index]);
                    attempts.Add(attempt);
                    break;
                }
                catch (BrokerJobFailedException) when (
                    index < fallbackModels.Length - 1)
                {
                }
            }

            validation = markdown
                ? TranslationValidator.ValidateMarkdown(source, attempt.Text)
                : TranslationValidator.ValidatePlain(source, attempt.Text);
        }

        var experimentalModel = experimentalAttempt.Receipts
            .Select(receipt => receipt.Routing)
            .Select(routing => routing?.ExperimentalModel ??
                               (routing?.IsExperimentalAttempt == true
                                   ? routing.SelectedModel
                                   : null))
            .Where(model => model is not null)
            .Select(model => model!)
            .FirstOrDefault();
        if (experimentalModel is not null)
        {
            var allReceipts = attempts
                .SelectMany(candidate => candidate.Receipts)
                .ToArray();
            var totalInputTokens = attempts.Sum(candidate => candidate.InputTokens);
            var totalOutputTokens = attempts.Sum(candidate => candidate.OutputTokens);
            var sourceTokens = TokenEstimator.ForText(source);
            await client.CompleteExperimentAsync(
                workflowId,
                profile,
                experimentalModel,
                experimentalOutcome,
                new LocalExperimentTaskMetrics(
                    totalInputTokens,
                    totalOutputTokens,
                    checked(totalInputTokens + totalOutputTokens),
                    attempt.OutputTokens,
                    Math.Max(0, sourceTokens - attempt.OutputTokens),
                    allReceipts.Aggregate(
                        TimeSpan.Zero,
                        (total, receipt) =>
                            total +
                            receipt.QueueDuration +
                            receipt.ExecutionDuration),
                    allReceipts.Count(receipt =>
                        receipt.Routing?.WasCold == true),
                    allReceipts.Count(receipt =>
                        receipt.Routing?.WasCold == false),
                    usedFallback),
                ct);
        }

        if (!validation.Passed)
        {
            throw new InvalidDataException(
                $"Local translation failed structural validation after fallback: " +
                $"{validation.Detail}.");
        }

        var receipt = attempt.Receipt;
        var model = receipt.Routing?.SelectedModel ?? receipt.Model;
        var answer = TranslationAttribution.Append(
            attempt.Text,
            targetLanguage,
            model);
        var tokenMetrics = TokenEstimator.ForTranslation(source, attempt.Text);
        return new LocalTranslationResult(
            answer,
            tokenMetrics.EstimatedCloudGenerationTokensSaved,
            attempts.Sum(candidate =>
                checked(candidate.InputTokens + candidate.OutputTokens)),
            tokenMetrics.EstimatedNetCloudContextTokensSaved,
            model,
            validation,
            receipt);
    }

    private sealed record TranslationAttempt(
        string Text,
        LocalUsageReceipt Receipt,
        IReadOnlyList<LocalUsageReceipt> Receipts,
        int InputTokens,
        int OutputTokens);

    private sealed record TranslationWorkItem(
        string Text,
        bool IsTranslatable);

    private static (string Leading, string Text, string Trailing)
        SplitBoundaryLineBreaks(string text)
    {
        var start = 0;
        while (start < text.Length && text[start] is '\r' or '\n')
        {
            start++;
        }

        var end = text.Length;
        while (end > start && text[end - 1] is '\r' or '\n')
        {
            end--;
        }

        return (text[..start], text[start..end], text[end..]);
    }

    /// <summary>
    /// Keeps the head and the tail when a log is too long. The tail holds the failure and the
    /// summary; the head holds what was being built - cutting either one loses the diagnosis.
    /// </summary>
    private static async Task<(string Content, bool Truncated)> ReadBoundedTextAsync(
        string path,
        int budget,
        CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        var builder = new StringBuilder(Math.Min(budget, 64 * 1024));
        var buffer = new char[64 * 1024];
        while (builder.Length <= budget)
        {
            var slice = Math.Min(buffer.Length, budget + 1 - builder.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, slice), ct);
            if (read == 0)
            {
                return (builder.ToString(), false);
            }

            builder.Append(buffer, 0, read);
        }

        return (builder.ToString(0, budget), true);
    }

    private static string Clamp(string content)
    {
        if (content.Length <= MaxPromptChars)
        {
            return content;
        }

        var head = content[..(MaxPromptChars / 6)];
        var tail = content[^(MaxPromptChars - (MaxPromptChars / 6))..];
        return $"{head}\n\n[... {content.Length - MaxPromptChars} characters omitted from the middle ...]\n\n{tail}";
    }

    private static int SelectContext(int characters) =>
        characters switch
        {
            <= 4_000 => 2048,
            <= 12_000 => 4096,
            <= 24_000 => 8192,
            <= 48_000 => 16384,
            <= 96_000 => 32768,
            <= 192_000 => 65536,
            <= 384_000 => 131072,
            _ => 262144
        };

    private static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"No such file: {full}", full);
        }

        return full;
    }
}

public sealed record LocalTranslationResult(
    string Answer,
    int SavedTokens,
    int LocalTokensProcessed,
    int NetCloudContextTokensSaved,
    string Model,
    TranslationValidationResult Validation,
    LocalUsageReceipt Receipt)
{
    public string Notice =>
        $"🔧 Локально: {Model}. Перевод проверен: {Validation.Detail}. " +
        $"Локально обработано примерно {TokenEstimator.Describe(LocalTokensProcessed)} токенов; " +
        $"на облачной генерации сэкономлено примерно {TokenEstimator.Describe(SavedTokens)}; " +
        $"чистое сокращение облачного контекста — " +
        $"{TokenEstimator.Describe(NetCloudContextTokensSaved)}.";
}
