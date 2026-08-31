using LocalAi.Contracts.Activation;

namespace LocalAi.Contracts.Indexing;

/// <summary>
/// One repository, one sync at a time, across every process on the machine.
///
/// Git hooks, the CLI and the MCP index_refresh all launch `localai sync` independently,
/// and until #199 nothing serialized them: two runs shared the progress file, the
/// manifest, generation destinations and embedding checkpoints, and an older run could
/// publish itself as current over a newer one. The gate is a named mutex keyed by the
/// repository id — the normalized Git common directory hash — so every worktree, client
/// and CLI of one repository contends for one lock, the same way they already share one
/// index.
///
/// A mutex rather than a semaphore on purpose: a killed process abandons a mutex and the
/// next acquirer proceeds, while an abandoned semaphore would jam the repository until a
/// reboot. The price of a mutex is thread affinity — it must be released by the thread
/// that acquired it, and a sync is async from end to end — so the lease holds it on a
/// dedicated background thread: acquired there, parked there, released there.
/// </summary>
public static class RepositorySyncGate
{
    private const string MutexPrefix = "LocalAi.CodeSearch.Sync.";

    /// <summary>
    /// Acquires the repository's sync gate, waiting at most <paramref name="waitBudget"/>.
    /// Null means another sync holds it — the caller reports the named busy outcome rather
    /// than queueing behind a build that can run for minutes.
    /// </summary>
    public static RepositorySyncLease? TryAcquire(
        string repositoryId,
        TimeSpan waitBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        if (waitBudget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(waitBudget));
        }

        return RepositorySyncLease.Acquire(
            SecureNamedMutexName.Create(MutexPrefix, repositoryId),
            waitBudget,
            cancellationToken);
    }
}

public sealed class RepositorySyncLease : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _holder;
    private int _disposed;

    private RepositorySyncLease(Thread holder)
    {
        _holder = holder;
    }

    internal static RepositorySyncLease? Acquire(
        string mutexName,
        TimeSpan waitBudget,
        CancellationToken cancellationToken)
    {
        var acquired = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RepositorySyncLease? lease = null;
        var holder = new Thread(() =>
        {
            try
            {
                using var mutex = SecureNamedMutexFactory.Instance.Create(mutexName);
                bool owned;
                try
                {
                    owned = mutex.WaitOne(waitBudget, cancellationToken);
                }
                catch (Exception exception)
                {
                    acquired.TrySetException(exception);
                    return;
                }

                acquired.TrySetResult(owned);
                if (!owned)
                {
                    return;
                }

                // Parked, not polling: the thread exists only because the mutex must be
                // released by its acquirer. Dispose is what sets this, always.
                lease!._release.Wait();
                mutex.Release();
            }
            catch (Exception exception)
            {
                acquired.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "localai-sync-gate",
        };
        lease = new RepositorySyncLease(holder);
        holder.Start();
        try
        {
            if (acquired.Task.GetAwaiter().GetResult())
            {
                return lease;
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        lease.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        if (_holder.IsAlive)
        {
            _holder.Join();
        }

        _release.Dispose();
    }
}
