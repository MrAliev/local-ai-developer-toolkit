using System.Text;
using LocalAi.Contracts;

namespace LocalLm.Core;

public sealed record LocalResult(
    string Answer,
    int SavedTokens,
    string Model,
    string Detail,
    LocalUsageReceipt Receipt)
{
    /// <summary>The line the caller is expected to surface, so the saving is visible, not silent.</summary>
    public string Notice =>
        $"🔧 Локально: {Model}. {Detail}. Сэкономлено примерно {TokenEstimator.Describe(SavedTokens)} облачных токенов.";
}

/// <summary>
/// The delegated jobs themselves. Each one reports what it consumed locally so the caller can
/// state a measured saving rather than a guessed one.
/// </summary>
public sealed class LocalTasks(ILocalModelClient client)
{
    private const long MaxImageBytes = 30 * 1024 * 1024;

    /// <summary>
    /// Text sent to a local model in one call. Well inside these models' windows, and past this a
    /// single answer stops being trustworthy anyway.
    /// </summary>
    private const int MaxPromptChars = 700_000;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
    };

    public async Task<LocalResult> ReadImageAsync(
        IReadOnlyList<string> paths, string question, string? model, CancellationToken ct)
    {
        if (paths.Count == 0)
        {
            throw new ArgumentException("No image paths given.", nameof(paths));
        }

        var images = new List<string>(paths.Count);
        var wouldHaveCost = 0;
        var described = new List<string>(paths.Count);

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

            var info = ImageInfo.Read(full);
            wouldHaveCost += TokenEstimator.ForImage(info);
            described.Add($"{Path.GetFileName(full)} ({info.Width}x{info.Height})");
            images.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(full, ct)));
        }

        var chosen = model ?? LocalModels.Vision;
        var result = await client.ChatAsync(
            chosen,
            question,
            "You are reading images on behalf of another engineer who will never see them. Answer only " +
            "from what is actually visible. Transcribe text exactly, preserving the original language. " +
            "If something is unreadable, say so explicitly instead of guessing.",
            images,
            LocalJobPriority.Foreground,
            ct);

        return new LocalResult(
            result.Value,
            TokenEstimator.Saved(wouldHaveCost, result.Value),
            chosen,
            $"прочитано изображений: {images.Count} — {string.Join(", ", described)}",
            result.Receipt);
    }

    public async Task<LocalResult> TriageLogAsync(
        string path, string? question, string? model, CancellationToken ct)
    {
        var full = Resolve(path);
        var content = await File.ReadAllTextAsync(full, ct);
        var originalLength = content.Length;
        var wouldHaveCost = TokenEstimator.ForText(content);

        content = Clamp(content);

        var chosen = model ?? LocalModels.Text;
        var result = await client.ChatAsync(
            chosen,
            $"""
             {question ?? "What failed, and why? Give the exact file and line if the log names one."}

             --- BEGIN LOG ({Path.GetFileName(full)}) ---
             {content}
             --- END LOG ---
             """,
            "You are triaging a build or test log for an engineer who will not read it themselves. " +
            "Report only failures the log actually contains - never invent a failure that is not there, " +
            "and say plainly if nothing failed. Quote exact file paths, line numbers and error codes. " +
            "Preserve the original language of any quoted message.",
            null,
            LocalJobPriority.Foreground,
            ct);

        var sizeKb = originalLength / 1024;
        return new LocalResult(
            result.Value,
            TokenEstimator.Saved(wouldHaveCost, result.Value),
            chosen,
            $"разобран лог {Path.GetFileName(full)} ({sizeKb} КБ)",
            result.Receipt);
    }

    public async Task<LocalResult> AskAsync(
        string prompt, IReadOnlyList<string> files, string? model, CancellationToken ct)
    {
        var bundle = new StringBuilder();
        var wouldHaveCost = 0;
        var names = new List<string>();

        foreach (var path in files)
        {
            var full = Resolve(path);
            var content = await File.ReadAllTextAsync(full, ct);
            wouldHaveCost += TokenEstimator.ForText(content);
            names.Add(Path.GetFileName(full));

            bundle.AppendLine($"--- FILE: {full} ---")
                .AppendLine(content)
                .AppendLine();
        }

        var chosen = model ?? LocalModels.Text;
        var body = Clamp(bundle.ToString());

        var result = await client.ChatAsync(
            chosen,
            files.Count == 0 ? prompt : $"{prompt}\n\n{body}",
            "You are doing mechanical work for an engineer who will not read these files themselves. " +
            "Answer strictly from the supplied content, quote exact identifiers and line content, and " +
            "say explicitly when the files do not contain the answer rather than filling the gap.",
            null,
            LocalJobPriority.Foreground,
            ct);

        var detail = files.Count == 0
            ? "выполнен запрос без файлов"
            : $"обработано файлов: {files.Count} — {string.Join(", ", names.Take(5))}{(names.Count > 5 ? ", …" : string.Empty)}";

        return new LocalResult(
            result.Value,
            TokenEstimator.Saved(wouldHaveCost, result.Value),
            chosen,
            detail,
            result.Receipt);
    }

    /// <summary>
    /// Keeps the head and the tail when a log is too long. The tail holds the failure and the
    /// summary; the head holds what was being built - cutting either one loses the diagnosis.
    /// </summary>
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
