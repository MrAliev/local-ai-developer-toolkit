using System.ComponentModel;
using System.Text.Json;
using LocalAi.Contracts;
using LocalLm.Core;
using ModelContextProtocol.Server;

namespace LocalLm.Mcp;

[McpServerToolType]
public static class LocalLmTools
{
    [McpServerTool(Name = "read_image")]
    [Description("""
        Reads image files with a local vision model and returns what they contain, as text.
        Use this INSTEAD of reading an image yourself whenever the image is a file on disk:
        screenshots, PDF pages rendered to PNG, photographed tables, diagrams, scans.
        A single screenshot costs ~1.5K tokens to look at directly and ~0.2K through this tool;
        a 100-page PDF is the difference between ~200K and ~3K.
        Note: an image already pasted into the conversation is already paid for - this tool only
        saves anything for images that have NOT entered the context yet.
        Always surface the returned notice line so the delegation is visible.
        """)]
    public static async Task<string> ReadImage(
        LocalTasks tasks,
        [Description("Absolute paths to image files (.png, .jpg, .bmp, .gif, .webp). Several are read together in one call.")]
        string[] paths,
        [Description("What to extract or answer. Be specific: 'transcribe the error text', 'list every row of the table', 'what does this diagram show'.")]
        string question,
        [Description("Task mode: VisualAnalysis, Ocr, or ImageTranslation.")]
        string mode = "VisualAnalysis",
        [Description("Optional model override. Normally leave blank so the router can choose a resident eligible model.")]
        string? model = null,
        CancellationToken cancellationToken = default)
        => await Run(() => tasks.ReadImageAsync(
            paths,
            question,
            ParseProfile(mode),
            model,
            cancellationToken));

    [McpServerTool(Name = "triage_log")]
    [Description("""
        Feeds a log file or direct log text to a local model and returns what failed and why. Built
        for build and test output, but works for machine-generated text of any length: dependency
        dumps, SQL plans, verbose traces. Provide exactly one of path or text. The tool probes the
        largest context that actually fits in VRAM, streams bounded fragments sequentially, and
        hierarchically reduces their findings without loading an entire file into memory.
        Always surface the returned notice line so the delegation is visible.
        """)]
    public static async Task<string> TriageLog(
        LocalTasks tasks,
        [Description("Absolute path to the log file. Mutually exclusive with text.")]
        string? path = null,
        [Description("Log text supplied directly. Mutually exclusive with path.")]
        string? text = null,
        [Description("Optional focus. Defaults to 'what failed and why, with exact file and line'.")]
        string? question = null,
        [Description("Optional model override. Normally leave blank so the router chooses.")]
        string? model = null,
        CancellationToken cancellationToken = default)
        => await Run(() => tasks.TriageLogAsync(
            path,
            text,
            question,
            model,
            cancellationToken));

    [McpServerTool(Name = "ask_local")]
    [Description("""
        Runs a mechanical, low-judgement task over specific files on a local model: summarize this,
        list every method that does X, extract the TODOs, collect named identifiers, check these
        files against a convention.
        Use when you already know which files matter and the task does not need deep cross-file
        reasoning - a local 9-27B model is good at 'list' and 'summarize', not at architectural
        judgement or subtle bug analysis. Verify anything that matters before relying on it.
        Always surface the returned notice line so the delegation is visible.
        """)]
    public static async Task<string> AskLocal(
        LocalTasks tasks,
        [Description("The instruction for the local model. State exactly what shape of answer you want back.")]
        string prompt,
        [Description("Absolute paths whose full content is sent along with the prompt. May be empty for a file-less question.")]
        string[]? files = null,
        [Description("Routing profile such as ShortSummary, CodeAnalysis, CodeReview, Extraction, Classification, or Planning.")]
        string taskProfile = "ShortSummary",
        [Description("Optional model override. Normally leave blank so the router chooses.")]
        string? model = null,
        CancellationToken cancellationToken = default)
        => await Run(() => tasks.AskAsync(
            ParseProfile(taskProfile),
            prompt,
            files ?? [],
            model,
            cancellationToken));

    [McpServerTool(Name = "translate_local")]
    [Description("Translates text through the model-aware FIFO broker, validates structure, and appends attribution naming the actual model.")]
    public static async Task<string> TranslateLocal(
        LocalTasks tasks,
        [Description("Text to translate.")]
        string source,
        [Description("Source language name.")]
        string sourceLanguage,
        [Description("Target language name.")]
        string targetLanguage,
        [Description("True when Markdown/code/URLs/placeholders must be structurally preserved.")]
        bool markdown = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await tasks.TranslateAsync(
                source,
                sourceLanguage,
                targetLanguage,
                markdown,
                cancellationToken);
            return $"{result.Notice}\n\n{result.Answer}";
        }
        catch (Exception exception)
        {
            return $"Локальный перевод не выполнен: {exception.Message}";
        }
    }

    [McpServerTool(Name = "local_models_status")]
    [Description("Shows installed and resident models, recommended missing models, and per-task experiment state.")]
    public static async Task<string> LocalModelsStatus(
        ModelManagementTasks tasks,
        CancellationToken cancellationToken = default) =>
        Serialize(await tasks.GetStatusAsync(cancellationToken));

    [McpServerTool(Name = "local_model_preflight")]
    [Description("Loads one model/context through the FIFO broker without task content and returns full-VRAM residency proof.")]
    public static async Task<string> LocalModelPreflight(
        ModelManagementTasks tasks,
        string model,
        string catalogVersion,
        int contextTokens = 2048,
        CancellationToken cancellationToken = default) =>
        Serialize(await tasks.PreflightAsync(
            model,
            contextTokens,
            catalogVersion,
            cancellationToken));

    [McpServerTool(Name = "local_models_sync")]
    [Description("Queues installation of recommended missing models through the durable FIFO broker.")]
    public static async Task<string> LocalModelsSync(
        ModelManagementTasks tasks,
        CancellationToken cancellationToken = default) =>
        Serialize(await tasks.SyncRecommendedAsync(cancellationToken));

    [McpServerTool(Name = "local_model_experiment_report")]
    [Description("Returns timing, error, fallback, warm/cold, and estimated token-saving statistics for one task/model experiment pair.")]
    public static async Task<string> LocalModelExperimentReport(
        ModelManagementTasks tasks,
        string taskProfile,
        string model,
        CancellationToken cancellationToken = default) =>
        Serialize(await tasks.GetExperimentReportAsync(
            ParseProfile(taskProfile),
            model,
            cancellationToken));

    [McpServerTool(Name = "local_model_feedback")]
    [Description("Applies owner feedback to one task/model pair: Promote, ContinueExperiment, FallbackOnly, or Disable.")]
    public static async Task<string> LocalModelFeedback(
        ModelManagementTasks tasks,
        string taskProfile,
        string model,
        string action,
        CancellationToken cancellationToken = default) =>
        Serialize(await tasks.ApplyFeedbackAsync(
            ParseProfile(taskProfile),
            model,
            ParseEnum<ExperimentOwnerAction>(action, nameof(action)),
            cancellationToken));

    /// <summary>
    /// Turns a result into the text the caller sees: the notice line first, so the delegation and
    /// its saving are impossible to drop when relaying the answer, then the answer itself.
    /// Failures come back as readable text rather than a protocol error, which keeps a missing
    /// file or a stopped Ollama from looking like a broken tool.
    /// </summary>
    private static async Task<string> Run(Func<Task<LocalResult>> job)
    {
        try
        {
            var result = await job();
            return $"{result.Notice}\n\n{result.Answer}";
        }
        catch (FileNotFoundException ex)
        {
            return $"Файл не найден: {ex.FileName}";
        }
        catch (ArgumentException ex)
        {
            return $"Некорректный запрос: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Локальная модель не отработала: {ex.Message}";
        }
    }

    private static LocalTaskProfile ParseProfile(string value) =>
        ParseEnum<LocalTaskProfile>(value, "taskProfile");

    private static T ParseEnum<T>(string value, string parameterName)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unknown {parameterName} '{value}'.",
                parameterName);

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, LocalAiJson.Strict);
}
