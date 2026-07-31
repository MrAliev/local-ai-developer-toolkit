using System.Runtime.Versioning;
using System.ComponentModel;
using LocalAi.Contracts.Activation;

namespace LocalAi.Installer.Core.Activation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerTransactionLease : IDisposable
{
    private const string MutexPrefix = "LocalAi.Installer.Transaction.";
    private readonly Worker worker;
    private FileStream? transactionFile;
    private bool disposed;

    private InstallerTransactionLease(Worker worker)
    {
        this.worker = worker;
    }

    public static InstallerTransactionLease Acquire(
        InstallationLayout layout,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Acquire(
            layout,
            timeout,
            cancellationToken,
            SecureNamedMutexFactory.Instance);

    internal static InstallerTransactionLease Acquire(
        InstallationLayout layout,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ISecureNamedMutexFactory mutexFactory)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var worker = new Worker(
            MutexName(layout.Root),
            timeout,
            cancellationToken,
            mutexFactory);
        worker.Start();
        try
        {
            worker.WaitUntilReady();
            return new InstallerTransactionLease(worker);
        }
        catch
        {
            worker.Dispose();
            throw;
        }
    }

    public static async Task<InstallerTransactionLease> AcquireAsync(
        InstallationLayout layout,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var worker = new Worker(
            MutexName(layout.Root),
            timeout,
            cancellationToken,
            SecureNamedMutexFactory.Instance);
        worker.Start();
        try
        {
            await worker.WaitUntilReadyAsync().ConfigureAwait(false);
            return new InstallerTransactionLease(worker);
        }
        catch
        {
            worker.Dispose();
            throw;
        }
    }

    public void AttachLayout(InstallationLayoutLease layoutLease)
    {
        ArgumentNullException.ThrowIfNull(layoutLease);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (transactionFile is not null)
        {
            throw new InvalidOperationException("The installer transaction layout is already attached.");
        }

        layoutLease.Revalidate();
        var path = Path.Combine(layoutLease.Layout.InstallerDirectory, "transaction.lock");
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new LocalAiPackageInstallationException(
                    "The installer transaction lock is unsafe.");
            }

            transactionFile = stream;
            stream = null!;
            layoutLease.Revalidate();
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        transactionFile?.Dispose();
        worker.Dispose();
    }

    private static string MutexName(string root)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return SecureNamedMutexName.Create(MutexPrefix, canonical);
    }

    private sealed class Worker : IDisposable
    {
        private readonly string mutexName;
        private readonly TimeSpan timeout;
        private readonly CancellationToken cancellationToken;
        private readonly ISecureNamedMutexFactory mutexFactory;
        private readonly TaskCompletionSource ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release = new(false);
        private readonly Thread thread;
        private bool disposed;

        public Worker(
            string mutexName,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            ISecureNamedMutexFactory mutexFactory)
        {
            this.mutexName = mutexName;
            this.timeout = timeout;
            this.cancellationToken = cancellationToken;
            this.mutexFactory = mutexFactory;
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "LocalAi installer transaction lease",
            };
        }

        public void Start() => thread.Start();

        public void WaitUntilReady()
        {
            ready.Task.GetAwaiter().GetResult();
        }

        public Task WaitUntilReadyAsync() => ready.Task;

        private void Run()
        {
            ISecureNamedMutex? mutex = null;
            var owns = false;
            try
            {
                mutex = mutexFactory.Create(mutexName);
                var deadline = DateTimeOffset.UtcNow + timeout;
                while (!owns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining < TimeSpan.Zero)
                    {
                        throw new InstallerTransactionBusyException();
                    }

                    var slice = remaining > TimeSpan.FromMilliseconds(25)
                        ? TimeSpan.FromMilliseconds(25)
                        : remaining;
                    owns = mutex.WaitOne(slice);
                }

                ready.TrySetResult();
                release.Wait();
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or Win32Exception or
                    System.Security.SecurityException or IOException)
            {
                ready.TrySetException(new InstallerTransactionBusyException());
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
            finally
            {
                if (owns)
                {
                    mutex!.Release();
                }

                mutex?.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            release.Set();
            if (thread.IsAlive)
            {
                thread.Join();
            }

            release.Dispose();
        }
    }
}

internal sealed class InstallerTransactionBusyException : Exception
{
    public InstallerTransactionBusyException()
        : base("Another LocalAi installation is already in progress.")
    {
    }
}
