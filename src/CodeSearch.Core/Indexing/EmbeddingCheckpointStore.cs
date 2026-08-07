using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Indexing;

/// <summary>
/// Persists completed embedding batches independently from the index being assembled. Each batch
/// is written through to a new file and atomically renamed, so a crash can lose only the batch
/// that was active at the time. A later build restores vectors by canonical text hash.
/// </summary>
internal sealed class EmbeddingCheckpointStore
{
    private static readonly byte[] Magic = "ECP1"u8.ToArray();
    private const int MaximumDimension = 65_536;
    private const int MaximumBatchItems = 16_384;

    private readonly string _directory;
    private readonly string _model;
    private readonly int? _expectedDimension;
    private readonly Action<string> _log;
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

    public EmbeddingCheckpointStore(
        string directory,
        string model,
        int? expectedDimension = null,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (expectedDimension is <= 0 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDimension));
        }

        _directory = Path.GetFullPath(directory);
        _model = model;
        _expectedDimension = expectedDimension;
        _log = log ?? (_ => { });
        LoadExisting();
    }

    public bool TryGet(string canonicalText, out float[] vector) =>
        _vectors.TryGetValue(Fingerprint(canonicalText), out vector!);

    public void SaveBatch(IReadOnlyList<string> canonicalTexts, IReadOnlyList<float[]> vectors)
    {
        ArgumentNullException.ThrowIfNull(canonicalTexts);
        ArgumentNullException.ThrowIfNull(vectors);
        if (canonicalTexts.Count != vectors.Count ||
            canonicalTexts.Count is <= 0 or > MaximumBatchItems)
        {
            throw new ArgumentException("A checkpoint batch must contain matching texts and vectors.");
        }

        var dimension = vectors[0].Length;
        ValidateDimension(dimension);
        if (vectors.Any(vector => vector.Length != dimension))
        {
            throw new InvalidDataException("A checkpoint batch contains inconsistent vector dimensions.");
        }

        Directory.CreateDirectory(_directory);
        var final = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".batch");
        var temporary = final + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(_model);
                writer.Write(dimension);
                writer.Write(canonicalTexts.Count);
                for (var i = 0; i < canonicalTexts.Count; i++)
                {
                    writer.Write(Convert.FromHexString(Fingerprint(canonicalTexts[i])));
                    foreach (var value in vectors[i])
                    {
                        writer.Write(value);
                    }
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, final);
            for (var i = 0; i < canonicalTexts.Count; i++)
            {
                _vectors[Fingerprint(canonicalTexts[i])] = vectors[i];
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void LoadExisting()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "*.batch")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ReadBatch(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _log($"Ignoring invalid embedding checkpoint '{path}': {exception.Message}");
            }
        }
    }

    private void ReadBatch(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Embedding checkpoint magic is invalid.");
        }

        if (!string.Equals(reader.ReadString(), _model, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Embedding checkpoint model does not match.");
        }

        var dimension = reader.ReadInt32();
        ValidateDimension(dimension);
        var count = reader.ReadInt32();
        if (count is <= 0 or > MaximumBatchItems)
        {
            throw new InvalidDataException("Embedding checkpoint item count is invalid.");
        }

        var expectedRemaining = checked((long)count * (SHA256.HashSizeInBytes + (long)dimension * sizeof(float)));
        if (stream.Length - stream.Position != expectedRemaining)
        {
            throw new InvalidDataException("Embedding checkpoint length is invalid.");
        }

        for (var item = 0; item < count; item++)
        {
            var fingerprint = Convert.ToHexString(reader.ReadBytes(SHA256.HashSizeInBytes));
            var vector = new float[dimension];
            for (var index = 0; index < dimension; index++)
            {
                vector[index] = reader.ReadSingle();
            }

            _vectors[fingerprint] = vector;
        }
    }

    private void ValidateDimension(int dimension)
    {
        if (dimension is <= 0 or > MaximumDimension ||
            _expectedDimension is not null && dimension != _expectedDimension)
        {
            throw new InvalidDataException(
                $"Embedding checkpoint dimension {dimension} does not match the expected dimension.");
        }
    }

    private static string Fingerprint(string canonicalText)
    {
        ArgumentNullException.ThrowIfNull(canonicalText);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));
    }
}
