using System.Runtime.CompilerServices;
using System.Text;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>
/// Streams an arbitrarily long log through one VRAM-admissible model/context pair. Source
/// fragments and reduction groups are bounded independently, so neither a large file nor a large
/// number of partial findings has to be retained in memory.
/// </summary>
internal sealed class LogTriagePipeline(
    ILocalModelClient client,
    LogTriagePolicy policy)
{
    private const string DefaultQuestion =
        "What failed, and why? Give the exact file and line if the log names one.";
    private const string SystemPrompt =
        "You are triaging a build or test log for an engineer who will not read it themselves. " +
        "Report only failures the supplied material actually contains - never invent a failure " +
        "that is not there, and say plainly if nothing failed. Preserve exact file paths, line " +
        "numbers, timestamps, identifiers and error codes, and preserve the original language of " +
        "quoted messages.";

    public async Task<LocalResult> RunAsync(
        string? path,
        string? text,
        string? question,
        string? model,
        CancellationToken cancellationToken)
    {
        var source = ValidateSource(path, text);
        var focus = string.IsNullOrWhiteSpace(question) ? DefaultQuestion : question.Trim();
        var capacity = await SelectCapacityAsync(model, cancellationToken);
        var promptBudget = InputCharacterBudget(capacity.ContextTokens);
        var availableCharacters =
            promptBudget - focus.Length - SystemPrompt.Length - policy.PromptOverheadCharacters;
        if (availableCharacters < 128)
        {
            throw new ArgumentException(
                "The question and configured prompt overhead do not fit the selected context.",
                nameof(question));
        }

        var fragmentBudget = Math.Min(
            policy.MaximumFragmentCharacters,
            availableCharacters);
        var overlapCharacters = Math.Min(
            policy.MaximumOverlapCharacters,
            Math.Max(0, fragmentBudget / 16));
        var reductionBudget = Math.Min(
            policy.MaximumFragmentCharacters,
            availableCharacters);
        var maximumSummaryCharacters = MaximumSummaryCharacters(capacity.ContextTokens);
        var reducer = new HierarchicalReducer(
            reductionBudget,
            (summaries, level, ct) => ReduceAsync(
                summaries,
                level,
                focus,
                maximumSummaryCharacters,
                capacity,
                ct));

        long fragmentCount = 0;
        long originalTokens = 0;
        LocalUsageReceipt? lastReceipt = null;
        // Accumulated, not overwritten. A triage is one model call per fragment plus the
        // reduction rounds, and reporting the last one as the whole run understated a
        // hundred-second job as three seconds -- in a line whose purpose is to make the cost
        // of a local call visible. The saving beside it was accumulated all along.
        var queued = TimeSpan.Zero;
        var executed = TimeSpan.Zero;
        await foreach (var fragment in ReadFragmentsAsync(
                           source,
                           fragmentBudget,
                           overlapCharacters,
                           cancellationToken))
        {
            fragmentCount++;
            originalTokens = Math.Min(
                int.MaxValue,
                originalTokens + TokenEstimator.ForText(fragment.UniqueText));
            var result = await AnalyzeFragmentAsync(
                fragment.Content,
                fragmentCount,
                focus,
                maximumSummaryCharacters,
                capacity,
                cancellationToken);
            lastReceipt = result.Receipt;
            queued += result.Receipt.QueueDuration;
            executed += result.Receipt.ExecutionDuration;
            await reducer.AddAsync(result.Value, cancellationToken);
        }

        var reduced = await reducer.CompleteAsync(cancellationToken);
        if (reduced.Receipt is not null)
        {
            lastReceipt = reduced.Receipt;
            queued += reduced.Receipt.QueueDuration;
            executed += reduced.Receipt.ExecutionDuration;
        }

        var answer = reduced.Text;
        var receipt = (lastReceipt
            ?? throw new InvalidOperationException("Log triage produced no model receipt."))
            with { QueueDuration = queued, ExecutionDuration = executed };
        var detail = source.Path is null
            ? LocalLmText.LogTextRead(
                source.Text!.Length,
                fragmentCount,
                capacity.ContextTokens)
            : LocalLmText.LogFileRead(
                Path.GetFileName(source.Path),
                new FileInfo(source.Path).Length / 1024,
                fragmentCount,
                capacity.ContextTokens);
        return new LocalResult(
            answer,
            TokenEstimator.Saved((int)originalTokens, answer),
            capacity.Model,
            detail,
            receipt);
    }

    private async Task<Capacity> SelectCapacityAsync(
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var status = (await client.GetModelsStatusAsync(cancellationToken)).Value;
        var catalog = ModelRoutingCatalogResource.LoadDocument();
        if (!string.Equals(
                status.CatalogVersion,
                catalog.CatalogVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Local model catalog mismatch: client '{catalog.CatalogVersion}', " +
                $"broker '{status.CatalogVersion}'.");
        }

        var route = catalog.Routes.Single(candidate =>
            candidate.Profile == LocalTaskProfile.LogTriage);
        var permitted = route.Candidates
            .Concat(route.Fallbacks)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (modelOverride is not null &&
            !permitted.Contains(modelOverride, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Model '{modelOverride}' is not configured for log triage.",
                nameof(modelOverride));
        }

        var installed = status.InstalledModels.ToHashSet(StringComparer.Ordinal);
        var disabled = (status.DisabledContexts ?? [])
            .ToHashSet();
        var orderedModels = modelOverride is null
            ? OrderRouteModels(route, catalog, status)
            : [modelOverride];
        var attempted = new List<string>();
        foreach (var model in orderedModels)
        {
            var entry = catalog.Models.Single(candidate =>
                string.Equals(candidate.Tag, model, StringComparison.Ordinal));
            if (!installed.Contains(model) ||
                entry.Lifecycle == LocalModelLifecycle.Disabled ||
                !entry.Capabilities.Contains(LocalModelCapability.Text))
            {
                continue;
            }

            foreach (var contextTokens in entry.ContextTokens
                         .Where(value => value <= policy.MaximumContextTokens)
                         .OrderDescending())
            {
                if (disabled.Contains(new ModelContextRef(model, contextTokens)))
                {
                    continue;
                }

                attempted.Add($"{model}/{contextTokens}");
                try
                {
                    var proof = (await client.PreflightModelAsync(
                        model,
                        contextTokens,
                        catalog.CatalogVersion,
                        cancellationToken)).Value;
                    return new Capacity(proof.Model, proof.ContextTokens);
                }
                catch (BrokerJobFailedException exception) when (
                    exception.FailureCode is
                        "ModelPreflightException" or
                        nameof(InvalidOperationException))
                {
                    // ModelRuntime disables only this failed model/context pair. Trying the next
                    // smaller tier is the actual capacity probe, not a guessed hardware profile.
                }
            }
        }

        throw new InvalidOperationException(
            "No full-VRAM log-triage model/context is available. Tried: " +
            (attempted.Count == 0 ? "none" : string.Join(", ", attempted)) + ".");
    }

    private static IReadOnlyList<string> OrderRouteModels(
        TaskRouteEntry route,
        ModelRoutingCatalogDocument catalog,
        LocalModelsStatusOutput status)
    {
        var experimentByModel = (status.Experiments ?? [])
            .Where(experiment => experiment.Profile == LocalTaskProfile.LogTriage)
            .ToDictionary(experiment => experiment.Model, StringComparer.Ordinal);
        var entries = catalog.Models.ToDictionary(model => model.Tag, StringComparer.Ordinal);
        var activeExperimental = route.Candidates.Where(tag =>
        {
            var entry = entries[tag];
            if (entry.Lifecycle != LocalModelLifecycle.Experimental)
            {
                return false;
            }

            return !experimentByModel.TryGetValue(tag, out var experiment) ||
                   (!experiment.IsPaused &&
                    !experiment.IsCircuitOpen &&
                    experiment.OwnerAction is not (
                        ExperimentOwnerAction.FallbackOnly or
                        ExperimentOwnerAction.Disable));
        });
        var established = route.Candidates.Where(tag =>
            entries[tag].Lifecycle != LocalModelLifecycle.Experimental ||
            experimentByModel.TryGetValue(tag, out var experiment) &&
            experiment.IsPromoted);
        return activeExperimental
            .Concat(established)
            .Concat(route.Fallbacks)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<LocalJobResult<string>> AnalyzeFragmentAsync(
        string content,
        long fragmentIndex,
        string question,
        int maximumSummaryCharacters,
        Capacity capacity,
        CancellationToken cancellationToken) =>
        await client.RoutedChatAsync(
            LocalTaskProfile.LogTriage,
            $"""
             {question}

             Analyze log fragment {fragmentIndex}. Return only relevant findings from this
             fragment. Keep the response under {maximumSummaryCharacters} characters.
             Preserve exact evidence. If the fragment contains no relevant failure, say so briefly.

             --- BEGIN LOG FRAGMENT {fragmentIndex} ---
             {content}
             --- END LOG FRAGMENT {fragmentIndex} ---
             """,
            SystemPrompt,
            null,
            new LocalWorkloadMetadata(
                content.Length,
                maximumSummaryCharacters,
                1,
                0,
                0,
                LocalDurationClass.Short),
            workflow: null,
            modelOverride: capacity.Model,
            requestedContextTokens: capacity.ContextTokens,
            LocalJobPriority.Foreground,
            cancellationToken);

    private async Task<ReducedSummary> ReduceAsync(
        IReadOnlyList<string> summaries,
        int level,
        string question,
        int maximumSummaryCharacters,
        Capacity capacity,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        for (var index = 0; index < summaries.Count; index++)
        {
            body.AppendLine($"--- PARTIAL FINDING {index + 1} ---")
                .AppendLine(summaries[index]);
        }

        var result = await client.RoutedChatAsync(
            LocalTaskProfile.LogTriage,
            $"""
             {question}

             Merge these ordered partial log findings (reduction level {level}). Deduplicate repeated
             overlap evidence, preserve every distinct failure and its exact evidence, and do not
             introduce claims absent from the partial findings. Return one concise diagnosis under
             {maximumSummaryCharacters} characters.

             {body}
             """,
            SystemPrompt,
            null,
            new LocalWorkloadMetadata(
                body.Length,
                maximumSummaryCharacters,
                summaries.Count,
                0,
                0,
                LocalDurationClass.Short),
            workflow: null,
            modelOverride: capacity.Model,
            requestedContextTokens: capacity.ContextTokens,
            LocalJobPriority.Foreground,
            cancellationToken);
        return new ReducedSummary(result.Value, result.Receipt);
    }

    private static async IAsyncEnumerable<LogFragment> ReadFragmentsAsync(
        LogSource source,
        int fragmentCharacters,
        int overlapCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (source.Text is not null)
        {
            var offset = 0;
            var overlap = string.Empty;
            if (source.Text.Length == 0)
            {
                yield return new LogFragment(string.Empty, string.Empty);
                yield break;
            }

            while (offset < source.Text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var freshLength = Math.Min(
                    Math.Max(1, fragmentCharacters - overlap.Length),
                    source.Text.Length - offset);
                freshLength = IncludeTrailingLowSurrogate(
                    source.Text,
                    offset,
                    freshLength);
                var fresh = source.Text.Substring(offset, freshLength);
                var content = overlap + fresh;
                yield return new LogFragment(content, fresh);
                offset += freshLength;
                overlap = Tail(content, overlapCharacters);
            }

            yield break;
        }

        await using var stream = new FileStream(
            source.Path!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: false);
        var fileOverlap = string.Empty;
        var yielded = false;
        while (true)
        {
            var freshBudget = Math.Max(1, fragmentCharacters - fileOverlap.Length);
            var fresh = await ReadAtMostAsync(reader, freshBudget, cancellationToken);
            if (fresh.Length == 0)
            {
                if (!yielded)
                {
                    yield return new LogFragment(string.Empty, string.Empty);
                }

                yield break;
            }

            yielded = true;
            var content = fileOverlap + fresh;
            yield return new LogFragment(content, fresh);
            fileOverlap = Tail(content, overlapCharacters);
        }
    }

    private static async Task<string> ReadAtMostAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[maximumCharacters];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await reader.ReadAsync(
                buffer.AsMemory(read, buffer.Length - read),
                cancellationToken);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read > 0 && char.IsHighSurrogate(buffer[read - 1]))
        {
            var next = new char[1];
            if (await reader.ReadAsync(next, cancellationToken) == 1)
            {
                return new string(buffer, 0, read) + next[0];
            }
        }

        return new string(buffer, 0, read);
    }

    private static int IncludeTrailingLowSurrogate(
        string text,
        int offset,
        int length) =>
        offset + length < text.Length &&
        length > 0 &&
        char.IsHighSurrogate(text[offset + length - 1]) &&
        char.IsLowSurrogate(text[offset + length])
            ? length + 1
            : length;

    private static string Tail(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        var start = value.Length - maximumCharacters;
        if (start > 0 &&
            char.IsLowSurrogate(value[start]) &&
            char.IsHighSurrogate(value[start - 1]))
        {
            start--;
        }

        return value[start..];
    }

    private int InputCharacterBudget(int contextTokens)
    {
        var reservedTokens = ReservedContextTokens(contextTokens);
        return Math.Max(
            256,
            checked((int)Math.Floor(
                (contextTokens - reservedTokens) * policy.CharactersPerToken)));
    }

    private int MaximumSummaryCharacters(int contextTokens) => Math.Min(
        policy.MaximumPartialSummaryCharacters,
        Math.Max(
            256,
            checked((int)Math.Floor(
                ReservedContextTokens(contextTokens) * policy.CharactersPerToken))));

    private int ReservedContextTokens(int contextTokens) => Math.Min(
        policy.ReservedContextTokens,
        Math.Max(256, contextTokens / 3));

    private static LogSource ValidateSource(string? path, string? text)
    {
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var hasText = text is not null;
        if (hasPath == hasText)
        {
            throw new ArgumentException("Provide exactly one of path or text.");
        }

        if (!hasPath)
        {
            return new LogSource(null, text);
        }

        var full = Path.GetFullPath(path!);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"No such file: {full}", full);
        }

        return new LogSource(full, null);
    }

    private sealed class HierarchicalReducer(
        int characterBudget,
        Func<IReadOnlyList<string>, int, CancellationToken, Task<ReducedSummary>> reduce)
    {
        private readonly List<Level> levels = [];
        private LocalUsageReceipt? lastReceipt;

        public async Task AddAsync(string summary, CancellationToken cancellationToken)
        {
            foreach (var piece in Split(summary, Math.Max(256, characterBudget / 2)))
            {
                await AddPieceAsync(0, piece, cancellationToken);
            }
        }

        public async Task<ReducedSummary> CompleteAsync(CancellationToken cancellationToken)
        {
            for (var levelIndex = 0; ; levelIndex++)
            {
                EnsureLevel(levelIndex);
                var level = levels[levelIndex];
                var isHighest = levelIndex == levels.Count - 1;
                if (isHighest)
                {
                    if (level.Items.Count == 0)
                    {
                        return new ReducedSummary(
                            "The supplied log is empty; no failure is present.",
                            lastReceipt);
                    }

                    if (level.Items.Count == 1)
                    {
                        return new ReducedSummary(level.Items[0], lastReceipt);
                    }

                    var final = await reduce(
                        level.Items,
                        levelIndex,
                        cancellationToken);
                    lastReceipt = final.Receipt;
                    return final;
                }

                if (level.Items.Count == 0)
                {
                    continue;
                }

                var reduced = level.Items.Count == 1
                    ? new ReducedSummary(level.Items[0], lastReceipt)
                    : await reduce(level.Items, levelIndex, cancellationToken);
                if (reduced.Receipt is not null)
                {
                    lastReceipt = reduced.Receipt;
                }

                level.Clear();
                await AddPieceAsync(
                    levelIndex + 1,
                    reduced.Text,
                    cancellationToken);
            }
        }

        private async Task AddPieceAsync(
            int levelIndex,
            string piece,
            CancellationToken cancellationToken)
        {
            EnsureLevel(levelIndex);
            var level = levels[levelIndex];
            if (level.Items.Count > 0 &&
                level.Characters + piece.Length > characterBudget)
            {
                var reduced = await reduce(
                    level.Items,
                    levelIndex,
                    cancellationToken);
                lastReceipt = reduced.Receipt;
                level.Clear();
                await AddPieceAsync(
                    levelIndex + 1,
                    reduced.Text,
                    cancellationToken);
            }

            level.Add(piece);
        }

        private void EnsureLevel(int levelIndex)
        {
            while (levels.Count <= levelIndex)
            {
                levels.Add(new Level());
            }
        }

        private static IEnumerable<string> Split(string value, int maximumCharacters)
        {
            if (value.Length == 0)
            {
                yield return string.Empty;
                yield break;
            }

            for (var offset = 0; offset < value.Length;)
            {
                var length = Math.Min(maximumCharacters, value.Length - offset);
                length = IncludeTrailingLowSurrogate(value, offset, length);
                yield return value.Substring(offset, length);
                offset += length;
            }
        }

        private sealed class Level
        {
            public List<string> Items { get; } = [];

            public int Characters { get; private set; }

            public void Add(string value)
            {
                Items.Add(value);
                Characters = checked(Characters + value.Length);
            }

            public void Clear()
            {
                Items.Clear();
                Characters = 0;
            }
        }
    }

    private sealed record LogSource(string? Path, string? Text);

    private sealed record LogFragment(string Content, string UniqueText);

    private sealed record Capacity(string Model, int ContextTokens);

    private sealed record ReducedSummary(string Text, LocalUsageReceipt? Receipt);
}
