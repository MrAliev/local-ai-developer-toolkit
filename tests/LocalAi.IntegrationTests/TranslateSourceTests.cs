using System.Text;
using LocalAi.Cli;
using LocalAi.Cli.Resources;

namespace LocalAi.IntegrationTests;

/// <summary>
/// Reading the document `--in` names. The parser stays away from the disk, so everything that
/// can only be learned by opening a file is decided here, and every refusal names the resolved
/// path: a relative path run from the wrong directory is the commonest cause, and only the
/// resolved form shows it.
/// </summary>
public sealed class TranslateSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-translate-in-" + Guid.NewGuid().ToString("N"));

    public TranslateSourceTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// The reason `--in` exists at all: `File.ReadAllText` decodes UTF-8 and detects a BOM, while
    /// standard input arrives through the console's input code page, which this binary never
    /// sets. A Cyrillic document survives one route and not the other.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_named_file_is_read_as_utf8(bool withBom)
    {
        var path = Path.Combine(_root, "readme.md");
        File.WriteAllText(path, "Брокер держит модель.", new UTF8Encoding(withBom));

        var read = TranslateSource.Read(path);

        Assert.Null(read.Refusal);
        Assert.Equal("Брокер держит модель.", read.Text, StringComparer.Ordinal);
    }

    /// <summary>The same code `triage` gives for the same mistake, and the same exit.</summary>
    [Fact]
    public void A_path_with_no_file_at_it_is_refused()
    {
        var path = Path.Combine(_root, "reamde.md");

        var read = TranslateSource.Read(path);

        Assert.Equal("file_missing", read.Refusal!.Code, StringComparer.Ordinal);
        Assert.Equal(2, read.Exit);
        Assert.Contains(path, read.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A directory shares `file_missing` with the path that names nothing — the remedy is the
    /// same — but not its sentence, because the reader who typed one made a different mistake.
    /// </summary>
    [Fact]
    public void A_directory_is_not_a_document()
    {
        var read = TranslateSource.Read(_root);

        Assert.Equal("file_missing", read.Refusal!.Code, StringComparer.Ordinal);

        // Asserted against the catalogue rather than against an English word: this test says the
        // two sentences are different, and it has to say that in whichever language it runs.
        Assert.Equal(
            CliText.TranslateNotAFile(Path.GetFullPath(_root)),
            read.Refusal.Message,
            StringComparer.Ordinal);
        Assert.NotEqual(
            CliText.TranslateFileMissing(Path.GetFullPath(_root)),
            read.Refusal.Message,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The twin of the empty pipe, sharing its code: whitespace only is empty here too, because
    /// that is what the task's own guard would reject a moment later with a vaguer sentence.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void A_file_with_nothing_in_it_is_refused_before_the_run(string content)
    {
        var path = Path.Combine(_root, "empty.md");
        File.WriteAllText(path, content);

        var read = TranslateSource.Read(path);

        Assert.Equal("source_missing", read.Refusal!.Code, StringComparer.Ordinal);
        Assert.Equal(2, read.Exit);
    }

    /// <summary>
    /// A zero byte is the one thing no text file has. The check is what the sentence claims and
    /// no more: the first 8000 bytes, and nothing about the content beyond that.
    /// </summary>
    [Fact]
    public void A_file_that_is_not_text_is_refused_rather_than_sent_to_a_model()
    {
        var path = Path.Combine(_root, "picture.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A, 0x1A]);

        var read = TranslateSource.Read(path);

        Assert.Equal("file_not_text", read.Refusal!.Code, StringComparer.Ordinal);
        Assert.Equal(2, read.Exit);
    }

    /// <summary>
    /// The exemption that makes the zero-byte scan safe. UTF-16 with a BOM decodes correctly and
    /// is full of zero bytes, so scanning it would refuse a document that reads perfectly; a
    /// BOM-less UTF-16 file has no correct decode here, and the scan is what catches it.
    /// </summary>
    [Fact]
    public void Utf16_with_a_byte_order_mark_is_text_and_is_read()
    {
        var path = Path.Combine(_root, "utf16.md");
        File.WriteAllText(path, "Брокер держит модель.", new UnicodeEncoding(false, true));

        var read = TranslateSource.Read(path);

        Assert.Null(read.Refusal);
        Assert.Equal("Брокер держит модель.", read.Text, StringComparer.Ordinal);
    }

    /// <summary>
    /// 66 is EX_NOINPUT, the mirror of the 73 `--out` gives when it cannot write. Not 2: the
    /// command line was right, and the cause is a lock or a permission.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_be_opened_exits_where_a_file_that_cannot_be_written_does()
    {
        var path = Path.Combine(_root, "locked.md");
        File.WriteAllText(path, "text");

        using var exclusive = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None);

        var read = TranslateSource.Read(path);

        Assert.Equal("input_not_read", read.Refusal!.Code, StringComparer.Ordinal);
        Assert.Equal(66, read.Exit);
    }

    /// <summary>
    /// Forgetting `--markdown` on a Markdown file sends fenced code to the model as prose and
    /// nobody notices until later; the opposite mistake costs nothing anybody wants. `.mdx` is
    /// out: JSX outside a fence is mangled either way, so protecting it would promise something
    /// the profile does not keep.
    /// </summary>
    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("README.MD", true)]
    [InlineData("notes.markdown", true)]
    [InlineData("page.mdx", false)]
    [InlineData("notes.txt", false)]
    [InlineData("LICENCE", false)]
    public void Markdown_is_recognised_by_extension(string name, bool expected) =>
        Assert.Equal(expected, TranslateSource.IsMarkdown(name));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file the test left open on a machine that has not released it yet is not a
            // failure of the thing under test.
        }
    }
}
