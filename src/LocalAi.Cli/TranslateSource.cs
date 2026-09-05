using System.Text;
using LocalAi.Cli.Resources;

namespace LocalAi.Cli;

/// <summary>
/// What reading the document <c>--in</c> named produced: its text, or the refusal that says why
/// there is none, with the exit code that refusal carries.
/// </summary>
public sealed record TranslateSourceRead(string? Text, CommandRefusal? Refusal, int Exit);

/// <summary>
/// Reads the document <c>--in</c> names.
///
/// Separate from <c>TranslateCommand</c> because the parser touches no disk — that is what lets
/// it be tested with literal paths — and separate from the entry point because everything here
/// has an answer worth asserting. Every refusal names the *resolved* path: a relative path run
/// from the wrong directory is the commonest cause, and only the resolved form shows it.
/// </summary>
public static class TranslateSource
{
    /// <summary>
    /// Not <c>.mdx</c>: JSX outside a fence is mangled either way, so protecting it would promise
    /// something the translation profile does not keep.
    /// </summary>
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    /// <summary>
    /// Enough of the file to answer "is this text at all". The sentence the refusal prints names
    /// this bound, so the two cannot drift apart.
    /// </summary>
    private const int SniffedBytes = 8000;

    public static bool IsMarkdown(string path) =>
        MarkdownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static TranslateSourceRead Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = Path.GetFullPath(path);

        // Before File.Exists, which answers false for a directory and would then produce the
        // sentence for a path that names nothing — true, but not the mistake that was made.
        if (Directory.Exists(resolved))
        {
            return new TranslateSourceRead(
                null,
                new CommandRefusal("file_missing", CliText.TranslateNotAFile(resolved)),
                2);
        }

        if (!File.Exists(resolved))
        {
            return new TranslateSourceRead(
                null,
                new CommandRefusal("file_missing", CliText.TranslateFileMissing(resolved)),
                2);
        }

        string text;
        try
        {
            using var stream = new FileStream(
                resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[SniffedBytes];
            var sniffed = stream.Read(head);
            if (!HasWideByteOrderMark(head.AsSpan(0, sniffed)) &&
                head.AsSpan(0, sniffed).IndexOf((byte)0) >= 0)
            {
                return new TranslateSourceRead(
                    null,
                    new CommandRefusal("file_not_text", CliText.TranslateFileNotText(resolved)),
                    2);
            }

            stream.Position = 0;
            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 66 is EX_NOINPUT, the mirror of the 73 `--out` gives when it cannot write, and for
            // the same reason: the command line was right, and the cause is a lock or a
            // permission.
            return new TranslateSourceRead(
                null,
                new CommandRefusal(
                    "input_not_read",
                    CliText.TranslateFileNotRead(resolved, failure.Message)),
                66);
        }

        // Whitespace only is empty here too: that is what the task's own guard would reject a
        // moment later, with a vaguer sentence and the wrong code.
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslateSourceRead(
                null,
                new CommandRefusal("source_missing", CliText.TranslateFileEmpty(resolved)),
                2);
        }

        return new TranslateSourceRead(text, null, 0);
    }

    /// <summary>
    /// The exemption that makes the zero-byte scan safe. UTF-16 and UTF-32 with a mark decode
    /// correctly and are full of zero bytes; without a mark there is no correct decode here, and
    /// the scan is exactly what catches that. A UTF-8 mark needs no exemption — it has no zero
    /// byte, and neither does the text after it.
    /// </summary>
    private static bool HasWideByteOrderMark(ReadOnlySpan<byte> head) =>
        (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) ||
        (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) ||
        (head.Length >= 4 &&
         head[0] == 0x00 && head[1] == 0x00 && head[2] == 0xFE && head[3] == 0xFF);
}
