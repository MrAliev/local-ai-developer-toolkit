using System.Buffers.Binary;

namespace LocalLm.Core;

/// <summary>
/// Pixel dimensions of an image, read straight from the file header.
///
/// Deliberately hand-parsed instead of pulling in an imaging library: the only thing needed is
/// width and height, and cloud image cost is driven by pixel count, not by file size - a 200KB
/// screenshot and a 200KB photo cost wildly different amounts.
/// </summary>
public sealed record ImageInfo(int Width, int Height, string Format)
{
    /// <summary>Used when a format's header can't be parsed, so an estimate is still possible.</summary>
    public static readonly ImageInfo Unknown = new(1600, 1200, "unknown");

    public static ImageInfo Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[32];
            var read = stream.Read(head);
            if (read < 24)
            {
                return Unknown;
            }

            if (IsPng(head))
            {
                // IHDR is always the first chunk: width and height are big-endian at offsets 16/20.
                var width = BinaryPrimitives.ReadInt32BigEndian(head[16..20]);
                var height = BinaryPrimitives.ReadInt32BigEndian(head[20..24]);
                return new ImageInfo(width, height, "png");
            }

            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                return ReadJpeg(stream) ?? Unknown;
            }

            if (head[0] == 'B' && head[1] == 'M')
            {
                var width = BinaryPrimitives.ReadInt32LittleEndian(head[18..22]);
                var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..26]));
                return new ImageInfo(width, height, "bmp");
            }

            if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
            {
                var width = BinaryPrimitives.ReadUInt16LittleEndian(head[6..8]);
                var height = BinaryPrimitives.ReadUInt16LittleEndian(head[8..10]);
                return new ImageInfo(width, height, "gif");
            }

            return Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown;
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> head) =>
        head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G';

    /// <summary>
    /// Walks JPEG segments to the first Start-Of-Frame marker, which is the only place the real
    /// dimensions live. Segment lengths vary, so there is no fixed offset to read.
    /// </summary>
    private static ImageInfo? ReadJpeg(FileStream stream)
    {
        stream.Position = 2;
        Span<byte> buffer = stackalloc byte[9];

        while (stream.Position < stream.Length)
        {
            if (stream.Read(buffer[..2]) != 2 || buffer[0] != 0xFF)
            {
                return null;
            }

            var marker = buffer[1];
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (stream.Read(buffer[..2]) != 2)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]);

            // SOF0..SOF15, excluding the DHT/JPG/DAC markers interleaved in that range.
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (stream.Read(buffer[..5]) != 5)
                {
                    return null;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(buffer[1..3]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(buffer[3..5]);
                return new ImageInfo(width, height, "jpeg");
            }

            stream.Position += length - 2;
        }

        return null;
    }
}
