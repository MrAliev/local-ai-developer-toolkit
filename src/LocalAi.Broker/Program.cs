using System.Diagnostics;
using LocalAi.Broker;
using LocalAi.Contracts;

return await BrokerProgram.RunAsync(args);

internal static class BrokerProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out var runtimeRoot, out var ollamaUri))
        {
            Console.Error.WriteLine(
                "Usage: LocalAi.Broker serve --runtime <path> [--ollama <absolute-url>]");
            return 2;
        }

        new RuntimeAcl().Ensure(runtimeRoot);
        await using var instanceLease = TryAcquireInstanceLease(runtimeRoot);
        if (instanceLease is null)
        {
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var process = Process.GetCurrentProcess();
        var startedAt = new DateTimeOffset(
            process.StartTime.ToUniversalTime(),
            TimeSpan.Zero);
        var owner = new BrokerProcessState(
            process.Id,
            startedAt,
            DateTimeOffset.UtcNow,
            1);
        var stateStore = new BrokerRuntimeStateStore(runtimeRoot);
        stateStore.Publish(owner);

        try
        {
            var queue = new DurableQueue(runtimeRoot);
            using var transport = new OllamaTransport(ollamaUri);
            var host = new BrokerHost(
                queue,
                $"broker-{process.Id}",
                transport.ExecuteAsync);
            var heartbeat = PublishHeartbeatAsync(
                stateStore,
                owner,
                shutdown.Token);
            try
            {
                await host.RunAsync(shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                shutdown.Cancel();
                await ObserveCancellationAsync(heartbeat);
            }

            return 0;
        }
        finally
        {
            stateStore.DeleteIfOwnedBy(owner);
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task PublishHeartbeatAsync(
        BrokerRuntimeStateStore store,
        BrokerProcessState owner,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            store.Publish(owner with { HeartbeatAtUtc = DateTimeOffset.UtcNow });
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static FileStream? TryAcquireInstanceLease(string runtimeRoot)
    {
        try
        {
            return new FileStream(
                Path.Combine(runtimeRoot, "broker.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool TryParse(
        string[] args,
        out string runtimeRoot,
        out Uri ollamaUri)
    {
        runtimeRoot = string.Empty;
        ollamaUri = new Uri("http://127.0.0.1:11434/");
        if (args.Length < 3 ||
            !string.Equals(args[0], "serve", StringComparison.Ordinal) ||
            !string.Equals(args[1], "--runtime", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            return false;
        }

        runtimeRoot = Path.GetFullPath(args[2]);
        if (args.Length == 3)
        {
            return true;
        }

        if (args.Length != 5 ||
            !string.Equals(args[3], "--ollama", StringComparison.Ordinal) ||
            !Uri.TryCreate(args[4], UriKind.Absolute, out var parsedOllamaUri))
        {
            return false;
        }

        ollamaUri = parsedOllamaUri;
        return true;
    }
}
