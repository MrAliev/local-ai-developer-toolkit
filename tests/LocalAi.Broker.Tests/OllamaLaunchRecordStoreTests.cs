using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The record exists so that nothing is resolved at the moment of use. Everything here is about
/// refusing to hand back something that was not properly written: there is no default to fall
/// back to, because the point of the document is that somebody verified this path.
/// </summary>
public sealed class OllamaLaunchRecordStoreTests : IDisposable
{
    private const string Executable =
        @"C:\Users\someone\AppData\Local\Programs\Ollama\ollama.exe";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-launch-record-" + Guid.NewGuid().ToString("N"));

    public OllamaLaunchRecordStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void What_was_written_is_what_comes_back()
    {
        var store = new OllamaLaunchRecordStore(_root);

        store.Save(Executable, "0.5.0");

        var record = store.Read();
        Assert.NotNull(record);
        Assert.Equal(Executable, record.ExecutablePath);
        Assert.Equal("0.5.0", record.Version);
    }

    [Fact]
    public void A_machine_with_no_record_says_nothing_rather_than_guessing()
    {
        Assert.Null(new OllamaLaunchRecordStore(_root).Read());
    }

    /// <summary>
    /// A relative path would be resolved against whatever directory the broker happened to be
    /// started in, which is not a place anybody chose.
    /// </summary>
    [Theory]
    [InlineData(@"ollama.exe")]
    [InlineData(@"..\Ollama\ollama.exe")]
    public void A_path_that_is_not_fully_qualified_is_refused(string path)
    {
        Assert.Throws<ArgumentException>(
            () => new OllamaLaunchRecordStore(_root).Save(path, null));
    }

    [Fact]
    public void A_malformed_document_reads_as_nothing()
    {
        File.WriteAllText(
            Path.Combine(_root, OllamaLaunchRecord.FileName),
            "{ this is not json");

        Assert.Null(new OllamaLaunchRecordStore(_root).Read());
    }

    /// <summary>
    /// A document from a schema this build does not know is not partially believed.
    /// </summary>
    [Fact]
    public void A_document_from_another_schema_reads_as_nothing()
    {
        File.WriteAllText(
            Path.Combine(_root, OllamaLaunchRecord.FileName),
            $$"""{"SchemaVersion":99,"ExecutablePath":"{{Executable.Replace(@"\", @"\\")}}"}""");

        Assert.Null(new OllamaLaunchRecordStore(_root).Read());
    }

    /// <summary>
    /// A record whose path was emptied out is not a record; reading it back as one would have
    /// the broker start an empty string.
    /// </summary>
    [Fact]
    public void A_document_with_no_path_reads_as_nothing()
    {
        File.WriteAllText(
            Path.Combine(_root, OllamaLaunchRecord.FileName),
            """{"SchemaVersion":1,"ExecutablePath":"   "}""");

        Assert.Null(new OllamaLaunchRecordStore(_root).Read());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
