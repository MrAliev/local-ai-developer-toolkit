using System.Diagnostics;
using CodeSearch.Core.Indexing;
using LocalAi.Cli;
using LocalAi.Contracts.Indexing;

namespace LocalAi.IntegrationTests;

/// <summary>
/// One repository, one sync at a time (#199). The gate is a named mutex held on a
/// dedicated thread, so two leases in one process contend exactly like two processes do.
/// </summary>
public sealed class RepositorySyncGateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-gate-" + Guid.NewGuid().ToString("N"));

    public RepositorySyncGateTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void The_gate_admits_one_holder_and_reopens_on_dispose()
    {
        var repositoryId = Guid.NewGuid().ToString("N");

        var first = RepositorySyncGate.TryAcquire(repositoryId, TimeSpan.Zero);
        Assert.NotNull(first);
        Assert.Null(RepositorySyncGate.TryAcquire(
            repositoryId,
            TimeSpan.FromMilliseconds(50)));

        first.Dispose();

        using var third = RepositorySyncGate.TryAcquire(repositoryId, TimeSpan.Zero);
        Assert.NotNull(third);
    }

    [Fact]
    public void Cancelling_the_wait_propagates_instead_of_reporting_busy()
    {
        var repositoryId = Guid.NewGuid().ToString("N");
        using var held = RepositorySyncGate.TryAcquire(repositoryId, TimeSpan.Zero);
        Assert.NotNull(held);
        // The budget is deliberately far beyond the test: cancellation is the only way the
        // wait can end, so the outcome is deterministic on a machine of any speed.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.ThrowsAny<OperationCanceledException>(
            () => RepositorySyncGate.TryAcquire(
                repositoryId,
                TimeSpan.FromHours(1),
                cancellation.Token));
    }

    /// <summary>
    /// The named outcome the whole gate exists for: a sync that finds the repository busy
    /// says so and exits before touching any shared state — no progress record, no
    /// manifest, no Failed stamped over the other run's story.
    /// </summary>
    [Fact]
    public async Task A_sync_against_a_held_repository_exits_with_the_named_busy_outcome()
    {
        var repository = CreateCommittedRepository();
        var runtime = Path.Combine(_root, "runtime");
        var identity = RuntimeIndexLayout.Inspect(repository, runtime);
        using var held = RepositorySyncGate.TryAcquire(identity.RepositoryId, TimeSpan.Zero);
        Assert.NotNull(held);

        var error = await Assert.ThrowsAsync<RepositorySyncBusyException>(
            () => CodeSearchSyncCommand.ExecuteAsync(
                repository,
                cancellationToken: TestContext.Current.CancellationToken,
                runtimeRoot: runtime));

        Assert.Contains(identity.RepositoryId, error.Message, StringComparison.Ordinal);
        Assert.False(
            Directory.Exists(identity.RepositoryRuntimeRoot),
            "A busy sync must exit before writing any shared state.");
    }

    [Fact]
    public void Publishing_refuses_when_the_pointer_moved_since_planning()
    {
        var store = new GenerationStore(Path.Combine(_root, "repo"));
        var first = Publish(store, "aaa");
        var second = Publish(store, "bbb");
        store.SetCurrent(store.ReadManifest(first));
        var observedAtPlanning = store.ReadCurrent();
        // Another run publishes while this one is still building.
        store.SetCurrent(store.ReadManifest(second));

        var error = Assert.Throws<InvalidOperationException>(
            () => store.SetCurrent(store.ReadManifest(first), observedAtPlanning));

        Assert.Contains("current_pointer_changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(second, store.ReadCurrent()!.GenerationId);
    }

    [Fact]
    public void Publishing_with_the_observed_pointer_succeeds()
    {
        var store = new GenerationStore(Path.Combine(_root, "repo"));
        var first = Publish(store, "aaa");
        store.SetCurrent(store.ReadManifest(first), expectedCurrent: null);
        Assert.Equal(first, store.ReadCurrent()!.GenerationId);

        var second = Publish(store, "bbb");
        store.SetCurrent(store.ReadManifest(second), store.ReadCurrent());

        Assert.Equal(second, store.ReadCurrent()!.GenerationId);
    }

    private string CreateCommittedRepository()
    {
        var repository = Path.Combine(_root, "repo-under-test");
        Directory.CreateDirectory(repository);
        Git(repository, "init", "-b", "main");
        Git(repository, "config", "user.email", "gate@test");
        Git(repository, "config", "user.name", "gate");
        File.WriteAllText(Path.Combine(repository, "A.cs"), "class A {}");
        Git(repository, "add", "-A");
        Git(repository, "commit", "-m", "init");
        return repository;
    }

    private static void Git(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repository);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: " +
            process.StandardError.ReadToEnd());
    }

    private string Publish(GenerationStore store, string tree)
    {
        var source = Path.Combine(_root, tree + ".cidx");
        File.WriteAllText(source, "INDEX-" + tree);
        return store.PublishIndex(
                source,
                new GenerationIdentity(
                    "repo",
                    "commit-" + tree,
                    tree,
                    "test-model",
                    2,
                    1,
                    CodeIndex.CurrentVersion,
                    1,
                    1))
            .Identity.Id;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
