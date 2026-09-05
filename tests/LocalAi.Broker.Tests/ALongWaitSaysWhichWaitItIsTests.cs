using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// A job that has not finished is waiting for one of two different things, and they are
/// different faults: a queue that will not move is another client or a stalled broker, while a
/// long run is a slow model. The console cannot tell them apart on its own — only the poll loop
/// here knows which, and until now it kept that to itself.
/// </summary>
public sealed class ALongWaitSaysWhichWaitItIsTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "localai-waiting-" + Guid.NewGuid().ToString("N"));

    private readonly DurableQueue queue;
    private readonly StateObserver observer = new();
    private readonly BrokerClient client;

    public ALongWaitSaysWhichWaitItIsTests()
    {
        queue = new DurableQueue(root);
        client = new BrokerClient(
            queue,
            new AlwaysRunningProcess(),
            static (delay, token) => Task.Delay(TimeSpan.FromMilliseconds(5), token),
            TimeSpan.FromMinutes(5),
            observer: observer);
    }

    /// <summary>
    /// Waited for by condition rather than by duration: what is asserted is that both states are
    /// reported, and how long this machine takes to get from one to the other proves nothing.
    /// </summary>
    [Fact]
    public async Task Both_waits_are_reported_and_they_are_not_the_same_report()
    {
        var request = LocalJobRequestFactory.CreateListModels(
            "models:test",
            LocalJobPriority.Interactive);

        var pending = client.ExecuteAsync<ListModelsJobOutput>(
            request,
            TestContext.Current.CancellationToken);

        await observer.SawQueued.Task.WaitAsync(TestContext.Current.CancellationToken);

        var lease = await queue.LeaseNextAsync(
                "test-worker",
                cancellationToken: TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Expected queued work.");
        await observer.SawRunning.Task.WaitAsync(TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        await queue.CompleteAsync(
            lease.Request.JobId,
            lease.WorkerId,
            lease.LeaseId,
            JsonSerializer.SerializeToElement(
                new BrokerResponseEnvelope(
                    JsonSerializer.SerializeToElement(
                        new ListModelsJobOutput([]),
                        LocalAiJson.Strict),
                    ReceiptFactory.Create(lease.Request, now, now)),
                LocalAiJson.Strict),
            TestContext.Current.CancellationToken);

        await pending;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    private sealed class StateObserver : ILocalRunObserver
    {
        public TaskCompletionSource SawQueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SawRunning { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Report(LocalRunStep step)
        {
            if (step is not BrokerJobPending pending)
            {
                return;
            }

            if (pending.Running)
            {
                SawRunning.TrySetResult();
            }
            else
            {
                SawQueued.TrySetResult();
            }
        }
    }

    private sealed class AlwaysRunningProcess : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public BrokerBackendState? ReadBackendState() => null;
    }
}
