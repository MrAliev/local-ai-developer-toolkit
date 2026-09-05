using System.Globalization;
using System.Text;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

public sealed record LocalResult(
    string Answer,
    int SavedTokens,
    string Model,
    string Detail,
    LocalUsageReceipt Receipt,
    /// <summary>
    /// Whether the answer was formed from part of the input rather than all of it.
    ///
    /// The fact was always in <see cref="Detail"/>, folded into a sentence. A caller reading the
    /// machine face has to be able to see it without parsing prose, because it changes how the
    /// answer must be read. Only asking over files can truncate: triage reduces hierarchically
    /// and image reading refuses instead.
    /// </summary>
    bool Truncated = false)
{
    /// <summary>
    /// The line the caller is expected to surface, so what a local call cost and saved is
    /// visible rather than silent.
    ///
    /// The duration was measured all along — the broker records how long a job waited and how
    /// long it ran — and then went only into experiment telemetry, so a caller asked to report
    /// it could only guess. It comes from the receipt now.
    /// </summary>
    public string Notice => LocalLmText.Notice(
        Model,
        DescribeResidency(Receipt),
        Detail,
        DescribeDuration(),
        TokenEstimator.DescribeSaving(SavedTokens));

    /// <summary>
    /// The mark beside the model when it did not fit in video memory.
    ///
    /// Beside the model rather than at the end of the line, because the shortfall is a fact
    /// about the model rather than about the task, and because a reader who stops after the
    /// first clause has still seen it. Empty for a healthy call: a parenthesis on every line
    /// is how a line stops being read.
    ///
    /// The percentage is what makes it information rather than a warning — it says how much of
    /// the model actually arrived.
    /// </summary>
    internal static string DescribeResidency(LocalUsageReceipt receipt) =>
        receipt.Routing?.ResidencyShortfall switch
        {
            ResidencyShortfall.PartialOffload =>
                LocalLmText.ResidencyPartialOffload(receipt.Routing.VramResidentPercent ?? 0),
            ResidencyShortfall.Cpu => LocalLmText.ResidencyCpu,
            _ => string.Empty,
        };

    /// <summary>
    /// How long it took, and how much of that was waiting.
    ///
    /// The two are different stories: four seconds behind another client is a queue to look
    /// at, four seconds of inference is a model to look at. The wait is only named when it is
    /// a real share of the total, so the ordinary line stays short.
    /// </summary>
    private string DescribeDuration() => DescribeDuration(Receipt);

    /// <summary>
    /// Shared with the translation result, which reports the same way and would otherwise
    /// grow a second copy of this to drift against.
    /// </summary>
    internal static string DescribeDuration(LocalUsageReceipt receipt)
    {
        var total = receipt.QueueDuration + receipt.ExecutionDuration;
        var queued = receipt.QueueDuration;
        return queued >= TimeSpan.FromSeconds(0.5) && queued >= total * 0.2
            ? LocalLmText.DurationWithQueue(Seconds(total), Seconds(queued))
            : Seconds(total);
    }

    private static string Seconds(TimeSpan span) =>
        LocalLmText.DurationSeconds(span < TimeSpan.FromSeconds(10)
            ? span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
            : span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
}

/// <summary>
/// The delegated jobs themselves. Each one reports what it consumed locally so the caller can
/// state a measured saving rather than a guessed one.
/// </summary>
public sealed class LocalTasks
{
    private readonly ILocalModelClient client;
    private readonly LogTriagePolicyStore logTriagePolicies;

    /// <summary>
    /// Optional because only the console has a reader for it. An MCP server passes none and
    /// stays silent: progress on a stdio server's standard error lands in the host's log, where
    /// nobody is waiting for it.
    /// </summary>
    private readonly ILocalRunObserver? observer;

    public LocalTasks(ILocalModelClient client, ILocalRunObserver? observer = null)
        : this(
            client,
            new LogTriagePolicyStore(LogTriagePolicyStore.DefaultRuntimeRoot),
            observer)
    {
    }

    internal LocalTasks(
        ILocalModelClient client,
        LogTriagePolicyStore logTriagePolicies,
        ILocalRunObserver? observer = null)
    {
        this.client = client;
        this.logTriagePolicies = logTriagePolicies;
        this.observer = observer;
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
        if (!IsImageProfile(profile))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                LocalLmText.ImageProfileUnsupported);
        }

        if (paths.Count == 0)
        {
            throw new ArgumentException(LocalLmText.NoImagePaths, nameof(paths));
        }

        if (paths.Count > MaxImageCount)
        {
            throw new ArgumentException(
                LocalLmText.TooManyImages(paths.Count, MaxImageCount),
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
            if (!IsImageFile(full))
            {
                throw new ArgumentException(LocalLmText.NotAnImage(full), nameof(paths));
            }

            var length = new FileInfo(full).Length;
            if (length > MaxImageBytes)
            {
                throw new ArgumentException(LocalLmText.ImageTooLarge(
                    full,
                    length / 1024 / 1024,
                    MaxImageBytes / 1024 / 1024));
            }

            totalImageBytes = checked(totalImageBytes + length);
            if (totalImageBytes > MaxTotalImageBytes)
            {
                throw new ArgumentException(
                    LocalLmText.ImagesTooLargeTogether(MaxTotalImageBytes / 1024 / 1024),
                    nameof(paths));
            }

            var info = ImageInfo.Read(full);
            wouldHaveCost += TokenEstimator.ForImage(info);
            totalImagePixels = checked(
                totalImagePixels + (long)info.Width * info.Height);
            if (totalImagePixels > MaxTotalImagePixels)
            {
                throw new ArgumentException(
                    LocalLmText.ImagesTooManyPixels(MaxTotalImagePixels),
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
            LocalLmText.ImagesRead(images.Count, string.Join(", ", described)),
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
        new LogTriagePipeline(client, logTriagePolicies.Read(), observer)
            .RunAsync(path, text, question, model, ct);

    /// <summary>
    /// Whether a profile routes to a model this task can hold a text conversation with.
    ///
    /// Public because a caller has to be able to say which profiles it will accept *before*
    /// sending one — a console refusal that named a set the check would reject is the defect
    /// `HookEventUnknown` and `NativeOperationUnknown` were both written to avoid. One predicate,
    /// so the refusal and the check cannot disagree.
    /// </summary>
    /// <summary>
    /// Whether a profile routes to a model that can look at an image.
    ///
    /// Public for the reason <see cref="IsTextChatProfile"/> is: a caller has to be able to name
    /// the set it accepts before sending one, and a refusal that named a set this check would
    /// reject is the defect the listing refusals were all built to avoid. The catalogue's
    /// <c>ImageProfileUnsupported</c> hardcodes the same three names in two languages; this is
    /// the one that cannot fall out of step with the code.
    /// </summary>
    public static bool IsImageProfile(LocalTaskProfile profile) =>
        profile is
            LocalTaskProfile.VisualAnalysis or
            LocalTaskProfile.Ocr or
            LocalTaskProfile.ImageTranslation;

    /// <summary>
    /// Whether a path names a file this can read as an image, by its extension.
    ///
    /// Public so a caller can refuse a PDF before the call rather than after: inside, the answer
    /// is an argument failure among many, and a console reporting all of those the same way tells
    /// a program nothing it can act on. The same set, so the two cannot disagree.
    /// </summary>
    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsTextChatProfile(LocalTaskProfile profile) =>
        profile is not (
            LocalTaskProfile.PlainTranslation or
            LocalTaskProfile.TechnicalTranslation or
            LocalTaskProfile.ExactSearch or
            LocalTaskProfile.VectorEmbedding or
            LocalTaskProfile.Ocr or
            LocalTaskProfile.VisualAnalysis or
            LocalTaskProfile.ImageTranslation);

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
        if (!IsTextChatProfile(profile))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                LocalLmText.NotATextChatProfile(profile));
        }

        if (files.Count > MaxAskFiles)
        {
            throw new ArgumentException(
                LocalLmText.TooManyFiles(files.Count, MaxAskFiles),
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
            ? LocalLmText.InputTruncated(MaxPromptChars) +
              (omitted > 0 ? LocalLmText.FilesSkipped(omitted) : string.Empty)
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
            ? LocalLmText.PromptOnly
            : LocalLmText.FilesProcessed(
                files.Count,
                string.Join(", ", names.Take(5)),
                names.Count > 5 ? ", …" : string.Empty)) +
            boundedNote;

        return new LocalResult(
            result.Value,
            TokenEstimator.Saved(wouldHaveCost, result.Value),
            chosen,
            detail,
            result.Receipt,
            Truncated: boundedNote.Length > 0);
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
                // Before the call rather than after it: the total is known before the
                // first one, and it is the fact that decides whether the reader waits.
                observer?.Report(new TranslatingFragment(modelStep + 1, modelStepCount));
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
                        LocalLmText.TranslationNoReceipt),
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
                    // The counter is about to restart at 1, which without this line
                    // reads as a defect rather than as a second pass over the document.
                    observer?.Report(
                        new TranslationRetrying(validation.Detail, fallbackModels[index]));
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
                LocalLmText.TranslationValidationFailed(validation.Detail));
        }

        var everyCall = attempts.SelectMany(candidate => candidate.Receipts).ToArray();
        var receipt = attempt.Receipt with
        {
            QueueDuration = everyCall.Aggregate(
                TimeSpan.Zero,
                (total, each) => total + each.QueueDuration),
            ExecutionDuration = everyCall.Aggregate(
                TimeSpan.Zero,
                (total, each) => total + each.ExecutionDuration),
        };
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
    public string Notice => LocalLmText.TranslationNotice(
        Model,
        LocalResult.DescribeResidency(Receipt),
        Validation.Detail,
        LocalResult.DescribeDuration(Receipt),
        TokenEstimator.Describe(LocalTokensProcessed),
        TokenEstimator.Describe(SavedTokens),
        TokenEstimator.Describe(NetCloudContextTokensSaved));
}
