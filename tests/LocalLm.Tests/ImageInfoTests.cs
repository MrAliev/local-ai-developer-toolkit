using System.Buffers.Binary;
using LocalLm.Core;

namespace LocalLm.Tests;

public class ImageInfoTests : IDisposable
{
    private readonly List<string> _temp = [];

    [Fact]
    public void ReadsPngDimensionsFromTheIhdrChunk()
    {
        var path = Write("png", BuildPng(1920, 1080));
        var info = ImageInfo.Read(path);

        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal("png", info.Format);
    }

    [Fact]
    public void ReadsJpegDimensionsByWalkingToTheStartOfFrameMarker()
    {
        // The dimensions are not at a fixed offset in a JPEG - a naive reader that assumes one
        // gets whatever bytes happen to sit there, so the walk has to survive a leading segment.
        var path = Write("jpg", BuildJpeg(1024, 768));
        var info = ImageInfo.Read(path);

        Assert.Equal(1024, info.Width);
        Assert.Equal(768, info.Height);
        Assert.Equal("jpeg", info.Format);
    }

    [Fact]
    public void UnparseableFileFallsBackInsteadOfThrowing()
    {
        var path = Write("png", new byte[64]);
        var info = ImageInfo.Read(path);

        Assert.Equal(ImageInfo.Unknown, info);
    }

    [Fact]
    public void MissingFileFallsBack() =>
        Assert.Equal(ImageInfo.Unknown, ImageInfo.Read(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.png")));

    private static byte[] BuildPng(int width, int height)
    {
        var bytes = new byte[32];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        new byte[] { (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static byte[] BuildJpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        // A JFIF APP0 segment first, so the test proves the marker walk rather than a fixed offset.
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]);
        bytes.AddRange(new byte[14]);

        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        bytes.AddRange([(byte)(height >> 8), (byte)(height & 0xFF)]);
        bytes.AddRange([(byte)(width >> 8), (byte)(width & 0xFF)]);
        bytes.AddRange(new byte[10]);

        return bytes.ToArray();
    }

    private string Write(string extension, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"locallm-test-{Guid.NewGuid():N}.{extension}");
        File.WriteAllBytes(path, content);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
