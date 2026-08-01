using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LocalAi.Installer.Core.Releases;

internal interface IRetainedStagingFile : IDisposable
{
    VerifiedPackageFile Metadata { get; }

    void Revalidate();

    Stream OpenRead();

    byte[] ReadAllBytes(int maximumBytes);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRetainedStagingFile : IRetainedStagingFile
{
    private readonly SafeFileHandle handle;
    private readonly WindowsStagingRootLease.FileIdentity identity;
    private readonly string physicalPath;
    private readonly byte[] sha256;
    private bool disposed;

    internal WindowsRetainedStagingFile(
        string relativePath,
        string expectedPath,
        SafeFileHandle handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentNullException.ThrowIfNull(handle);
        this.handle = handle;
        identity = WindowsStagingRootLease.GetIdentity(handle);
        physicalPath = WindowsStagingRootLease.GetFinalPath(handle);
        if (identity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            identity.Attributes.HasFlag(FileAttributes.Directory) ||
            !string.Equals(
                physicalPath,
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure();
        }

        var length = RandomAccess.GetLength(handle);
        sha256 = ComputeSha256(handle, length);
        Metadata = new VerifiedPackageFile(
            relativePath,
            length,
            Convert.ToHexString(sha256));
        Revalidate();
    }

    public VerifiedPackageFile Metadata { get; }

    public void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = WindowsStagingRootLease.GetIdentity(handle);
        if (current != identity ||
            current.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            current.Attributes.HasFlag(FileAttributes.Directory) ||
            !string.Equals(
                WindowsStagingRootLease.GetFinalPath(handle),
                physicalPath,
                StringComparison.OrdinalIgnoreCase) ||
            RandomAccess.GetLength(handle) != Metadata.Length ||
            !CryptographicOperations.FixedTimeEquals(
                ComputeSha256(handle, Metadata.Length),
                sha256))
        {
            throw Failure();
        }
    }

    public Stream OpenRead()
    {
        Revalidate();
        return new HandleReadStream(Duplicate(handle));
    }

    public byte[] ReadAllBytes(int maximumBytes)
    {
        Revalidate();
        if (maximumBytes < 0 || Metadata.Length > maximumBytes)
        {
            throw Failure();
        }

        var result = new byte[checked((int)Metadata.Length)];
        var offset = 0;
        while (offset < result.Length)
        {
            var read = RandomAccess.Read(handle, result.AsSpan(offset), offset);
            if (read == 0)
            {
                throw Failure();
            }

            offset += read;
        }

        Revalidate();
        return result;
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            handle.Dispose();
        }
    }

    private static byte[] ComputeSha256(SafeFileHandle file, long expectedLength)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < expectedLength)
        {
            var requested = (int)Math.Min(buffer.Length, expectedLength - offset);
            var read = RandomAccess.Read(file, buffer.AsSpan(0, requested), offset);
            if (read == 0)
            {
                throw Failure();
            }

            hash.AppendData(buffer.AsSpan(0, read));
            offset += read;
        }

        if (RandomAccess.GetLength(file) != expectedLength)
        {
            throw Failure();
        }

        return hash.GetHashAndReset();
    }

    private static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        var addedRef = false;
        try
        {
            source.DangerousAddRef(ref addedRef);
            if (!DuplicateHandle(
                    GetCurrentProcess(),
                    source.DangerousGetHandle(),
                    GetCurrentProcess(),
                    out var duplicate,
                    0,
                    inheritHandle: false,
                    0x00000002))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return duplicate;
        }
        catch (Exception exception) when (
            exception is Win32Exception or ObjectDisposedException)
        {
            throw Failure();
        }
        finally
        {
            if (addedRef)
            {
                source.DangerousRelease();
            }
        }
    }

    private static ReleaseVerificationException Failure() =>
        new("Verified package file identity changed.");

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    private sealed class HandleReadStream(SafeFileHandle handle) : Stream
    {
        private long position;
        private bool disposed;

        public override bool CanRead => !disposed;

        public override bool CanSeek => !disposed;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return RandomAccess.GetLength(handle);
            }
        }

        public override long Position
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return position;
            }
            set
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var read = RandomAccess.Read(handle, buffer, position);
            position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var read = await RandomAccess.ReadAsync(
                handle,
                buffer,
                position,
                cancellationToken).ConfigureAwait(false);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < 0)
            {
                throw new IOException("Cannot seek before the beginning of the file.");
            }

            position = next;
            return position;
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                handle.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
