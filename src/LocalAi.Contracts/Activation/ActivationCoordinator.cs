using System.Diagnostics;
using System.ComponentModel;

namespace LocalAi.Contracts.Activation;

public static class ActivationCoordinator
{
    private const string MutexPrefix = "LocalAi.Launcher.Activation.";

    public static ActivationExclusiveLease AcquireExclusive(
        string binRoot,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var started = Stopwatch.GetTimestamp();
        var gate = AcquireStartupGate(binRoot, timeout);
        try
        {
            var remaining = Remaining(timeout, started);
            var result = AcquireExclusive(gate, remaining, ownsGate: true);
            gate = null!;
            return result;
        }
        finally
        {
            gate?.Dispose();
        }
    }

    public static ActivationStartupGateLease AcquireStartupGate(
        string binRoot,
        TimeSpan timeout) =>
        AcquireStartupGate(binRoot, timeout, SecureNamedMutexFactory.Instance);

    internal static ActivationStartupGateLease AcquireStartupGate(
        string binRoot,
        TimeSpan timeout,
        ISecureNamedMutexFactory mutexFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        ArgumentNullException.ThrowIfNull(mutexFactory);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var canonicalRoot = CanonicalRoot(binRoot);
        ISecureNamedMutex? mutex = null;
        try
        {
            mutex = mutexFactory.Create(MutexName(canonicalRoot));
            if (!mutex.WaitOne(timeout))
            {
                throw new ActivationCoordinationException(
                    "activation_timeout",
                    "Timed out waiting for another LocalAi activation.");
            }

            var result = new ActivationStartupGateLease(canonicalRoot, mutex);
            mutex = null!;
            return result;
        }
        catch (ActivationCoordinationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or Win32Exception or
                System.Security.SecurityException or IOException)
        {
            throw new ActivationCoordinationException(
                "activation_unavailable",
                "The LocalAi activation gate is unavailable.",
                exception);
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    public static ActivationExclusiveLease AcquireExclusive(
        ActivationStartupGateLease gate,
        TimeSpan timeout) =>
        AcquireExclusive(gate, timeout, ownsGate: false);

    private static ActivationExclusiveLease AcquireExclusive(
        ActivationStartupGateLease gate,
        TimeSpan timeout,
        bool ownsGate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var started = Stopwatch.GetTimestamp();
        var lockPath = Path.Combine(gate.BinRoot, "current.lock");
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                return new ActivationExclusiveLease(
                    gate.BinRoot,
                    stream,
                    ownsGate ? gate : null);
            }
            catch (IOException) when (Remaining(timeout, started) > TimeSpan.Zero)
            {
                Thread.Sleep(Minimum(Remaining(timeout, started), TimeSpan.FromMilliseconds(25)));
            }
            catch (IOException exception)
            {
                throw new ActivationCoordinationException(
                    "version_in_use",
                    "The active LocalAi version is currently in use.",
                    exception);
            }
        }
    }

    public static ActivationSharedLease AcquireShared(
        string binRoot,
        TimeSpan startupTimeout)
    {
        var canonicalRoot = CanonicalRoot(binRoot);
        using var gate = AcquireStartupGate(canonicalRoot, startupTimeout);
        try
        {
            var stream = new FileStream(
                Path.Combine(canonicalRoot, "current.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            return new ActivationSharedLease(canonicalRoot, stream);
        }
        catch (IOException exception)
        {
            throw new ActivationCoordinationException(
                "version_in_use",
                "The active LocalAi version is currently in use.",
                exception);
        }
    }

    public static ActivationSharedLease AcquireShared(string binRoot) =>
        AcquireShared(binRoot, TimeSpan.FromSeconds(30));

    public static string CanonicalRoot(string binRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(binRoot));

    internal static string MutexName(string canonicalRoot)
        => SecureNamedMutexName.Create(MutexPrefix, canonicalRoot);

    private static TimeSpan Remaining(TimeSpan timeout, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        return elapsed >= timeout ? TimeSpan.Zero : timeout - elapsed;
    }

    private static TimeSpan Minimum(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;
}

public sealed class ActivationStartupGateLease : IDisposable
{
    private ISecureNamedMutex? mutex;

    internal ActivationStartupGateLease(string binRoot, ISecureNamedMutex mutex)
    {
        BinRoot = binRoot;
        this.mutex = mutex;
    }

    public string BinRoot { get; }
    public string CurrentPath => Path.Combine(BinRoot, "current.json");

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref mutex, null);
        if (owned is null)
        {
            return;
        }

        owned.Release();
        owned.Dispose();
    }
}

public sealed class ActivationExclusiveLease : IDisposable
{
    private FileStream? stream;
    private ActivationStartupGateLease? ownedGate;

    internal ActivationExclusiveLease(
        string binRoot,
        FileStream stream,
        ActivationStartupGateLease? ownedGate)
    {
        BinRoot = binRoot;
        this.stream = stream;
        this.ownedGate = ownedGate;
    }

    public string BinRoot { get; }
    public string CurrentPath => Path.Combine(BinRoot, "current.json");
    public string LockPath => Path.Combine(BinRoot, "current.lock");

    public void Dispose()
    {
        var ownedStream = Interlocked.Exchange(ref stream, null);
        var gate = Interlocked.Exchange(ref ownedGate, null);
        ownedStream?.Dispose();
        gate?.Dispose();
    }
}

public sealed class ActivationSharedLease : IDisposable
{
    private FileStream? stream;

    internal ActivationSharedLease(string binRoot, FileStream stream)
    {
        BinRoot = binRoot;
        this.stream = stream;
    }

    public string BinRoot { get; }

    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}

public sealed class ActivationCoordinationException : Exception
{
    public ActivationCoordinationException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
