using System.Collections.ObjectModel;
using System.ComponentModel;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// A downloaded release whose manifest and files have both been verified, held open on the
/// staging root that owns them.
///
/// It stays here rather than beside the manifest format because it is not a format: it is a
/// lease on files this process has staged, and only the thing that installs a release has any
/// use for that. What the runtime needs to know about a release is the manifest, which is a
/// document; what the installer needs is this, which is a resource.
/// </summary>
public sealed class VerifiedPackage : IDisposable
{
    private readonly object gate = new();
    private readonly IReadOnlyDictionary<string, IRetainedStagingFile> retainedByPath;
    private IReadOnlyList<IRetainedStagingFile>? retainedFiles;
    private IStagingRootLease? stagingLease;

    internal VerifiedPackage(
        ReleaseManifest manifest,
        IStagingRootLease stagingLease,
        IReadOnlyList<IRetainedStagingFile> retainedFiles)
    {
        Manifest = manifest;
        this.stagingLease = stagingLease;
        this.retainedFiles = retainedFiles.ToArray();
        retainedByPath = this.retainedFiles.ToDictionary(
            file => file.Metadata.RelativePath,
            StringComparer.Ordinal);
        Files = new ReadOnlyCollection<VerifiedPackageFile>(
            this.retainedFiles.Select(file => file.Metadata).ToArray());
        DiagnosticStagingRoot = stagingLease.CanonicalPath;
    }

    public ReleaseManifest Manifest { get; }

    /// <summary>
    /// The immutable allowlist Task 6 must use when installing this package.
    /// Task 6 must not enumerate or copy the staging tree by path.
    /// </summary>
    public IReadOnlyList<VerifiedPackageFile> Files { get; }

    internal string DiagnosticStagingRoot { get; }

    /// <summary>
    /// Opens an independent read-only stream backed by the retained verified
    /// file handle. Task 6 must consume content only through this method.
    /// </summary>
    public Stream OpenRead(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        try
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (!retainedByPath.TryGetValue(relativePath, out var file))
                {
                    throw Failure();
                }

                stagingLease!.ValidateExactLayout(
                    Files.Select(approved => approved.RelativePath));
                file.Revalidate();
                stagingLease.Revalidate();
                return new SanitizedReadStream(file.OpenRead());
            }
        }
        catch (Exception exception) when (IsNativeBoundaryFailure(exception))
        {
            throw Failure();
        }
    }

    public void Revalidate()
    {
        try
        {
            lock (gate)
            {
                ThrowIfDisposed();
                RevalidateCore();
            }
        }
        catch (Exception exception) when (IsNativeBoundaryFailure(exception))
        {
            throw Failure();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            var files = retainedFiles;
            var lease = stagingLease;
            retainedFiles = null;
            stagingLease = null;
            if (files is null)
            {
                return;
            }

            try
            {
                foreach (var file in files)
                {
                    file.Dispose();
                }
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    /// <summary>
    /// Dispose plus the staging cleanup a successful install owes (#204): the verifier
    /// cleans up on failure, but every success used to leave a full unpacked copy of the
    /// package under the temp root forever, because Dispose only closes handles. The lease's
    /// own Cleanup revalidates ownership first and deletes nothing it cannot prove safe:
    /// a directory or reparse point inside — something a staging never contains — leaves
    /// the root in place for a human, and this returns false, with the handles closed
    /// either way. Success of the install never depends on this succeeding.
    /// </summary>
    public bool TryCleanupAndDispose()
    {
        lock (gate)
        {
            var files = retainedFiles;
            var lease = stagingLease;
            retainedFiles = null;
            stagingLease = null;
            if (files is null)
            {
                return true;
            }

            try
            {
                foreach (var file in files)
                {
                    file.Dispose();
                }
            }
            catch
            {
                lease?.Dispose();
                throw;
            }

            if (lease is null)
            {
                return true;
            }

            try
            {
                lease.Cleanup();
                return true;
            }
            catch (Exception exception) when (
                exception is ReleaseVerificationException or InvalidOperationException or
                    IOException or UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    private void RevalidateCore()
    {
        var lease = stagingLease!;
        var files = retainedFiles!;
        lease.ValidateExactLayout(Files.Select(file => file.RelativePath));
        foreach (var file in files)
        {
            file.Revalidate();
        }

        lease.Revalidate();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(retainedFiles is null, this);

    private static ReleaseVerificationException Failure() =>
        new("Verified package content is unavailable.");

    private static bool IsNativeBoundaryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        Win32Exception or System.Security.SecurityException;

    private sealed class SanitizedReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => Execute(() => inner.Length);

        public override long Position
        {
            get => Execute(() => inner.Position);
            set => Execute(() => inner.Position = value);
        }

        public override void Flush() => Execute(inner.Flush);

        public override int Read(byte[] buffer, int offset, int count) =>
            Execute(() => inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (Exception exception) when (IsNativeBoundaryFailure(exception))
            {
                throw Failure();
            }
        }

        public override int ReadByte() => Execute(inner.ReadByte);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNativeBoundaryFailure(exception))
            {
                throw Failure();
            }
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            try
            {
                return await inner.ReadAsync(buffer, offset, count, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNativeBoundaryFailure(exception))
            {
                throw Failure();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            Execute(() => inner.Seek(offset, origin));

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private static T Execute<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception exception) when (IsNativeBoundaryFailure(exception))
            {
                throw Failure();
            }
        }

        private static void Execute(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (IsNativeBoundaryFailure(exception))
            {
                throw Failure();
            }
        }
    }
}

public sealed class VerifiedPackageFile
{
    internal VerifiedPackageFile(string relativePath, long length, string sha256)
    {
        RelativePath = relativePath;
        Length = length;
        Sha256 = sha256;
    }

    public string RelativePath { get; }

    public long Length { get; }

    public string Sha256 { get; }
}
