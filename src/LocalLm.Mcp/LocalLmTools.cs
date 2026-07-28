using System.ComponentModel;
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
        [Description("Override the vision model. Defaults to qwen3-vl:8b-instruct-q8_0.")]
        string? model = null,
        CancellationToken cancellationToken = default)
        => await Run(() => tasks.ReadImageAsync(paths, question, model, cancellationToken));

    [McpServerTool(Name = "triage_log")]
    [Description("""
        Feeds a large log file to a local model and returns what failed and why. Built for build
        and test output, but works for any long machine-generated text: dependency dumps, SQL
        plans, verbose traces. A 600KB build log costs ~150K tokens to read directly and a few
        hundred through this tool. Reads the head and tail when a log exceeds the local window.
        Always surface the returned notice line so the delegation is visible.
        """)]
    public static async Task<string> TriageLog(
        LocalTasks tasks,
        [Description("Absolute path to the log file.")]
        string path,
        [Description("Optional focus. Defaults to 'what failed and why, with exact file and line'.")]
        string? question = null,
        [Description("Override the model. Defaults to qwen3.6:27b.")]
        string? model = null,
        CancellationToken cancellationToken = default)
        => await Run(() => tasks.TriageLogAsync(path, question, model, cancellationToken));

    [McpServerTool(Name = "ask_local")]
    [Description("""
        Runs a mechanical, low-judgement task over specific files on a local model: summarize this,
        list every method that does X, extract the TODOs, draft an English translation of these
        Russian commit messages, check these files against a convention.
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
        [Description("Override the model. Defaults to qwen3.6:27b.")]
        string? model = null,
        CancellationToken cancellationToken = default)
        => await Run(() => tasks.AskAsync(prompt, files ?? [], model, cancellationToken));

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
}
