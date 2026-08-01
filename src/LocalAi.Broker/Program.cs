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
        // Environment.ProcessPath rather than Assembly.Location: the broker ships as a
        // single-file self-contained executable, and Location is empty when bundled.
        var brokerAssemblyPath = Path.GetFullPath(
            Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The broker could not determine its own executable path."));
        var owner = new BrokerProcessState(
            process.Id,
            startedAt,
            DateTimeOffset.UtcNow,
            BrokerCompatibilityContract.HostStateSchemaVersion,
            brokerAssemblyPath,
            BrokerCompatibilityContract.Current);
        var stateStore = new BrokerRuntimeStateStore(runtimeRoot);
        stateStore.Publish(owner);

        try
        {
            var queue = new DurableQueue(runtimeRoot);
            using var transport = new OllamaTransport(ollamaUri);
            var catalog = ModelRoutingCatalog.LoadEmbedded();
            var policy = new ModelResidencyPolicyStore(runtimeRoot).Read();
            var runtime = new ModelRuntime(
                transport,
                catalog,
                residencyPolicy: policy.ModelResidency);
            if (policy.ModelResidency != ModelResidencyPolicy.RequireFullVram)
            {
                Console.Error.WriteLine(
                    "LocalAi broker: model residency policy is relaxed to " +
                    $"{policy.ModelResidency}. Responses may be substantially slower than a " +
                    "fully resident load.");
            }

            var experiments = new ExperimentStateStore(runtimeRoot);
            var telemetry = new ModelTelemetryStore(runtimeRoot);
            var coordinator = new ModelExecutionCoordinator(
                new ModelRouter(catalog),
                runtime,
                experiments,
                telemetry,
                transport.ExecuteAsync);
            var control = new ModelControlService(
                catalog,
                transport,
                experiments,
                telemetry,
                runtime,
                queue);
            var executionRouter = new BrokerExecutionRouter(
                catalog,
                transport,
                runtime,
                coordinator,
                control,
                transport.ExecuteAsync);
            var durationEstimator = new DurationEstimator();
            var scheduleMetadata = new ScheduleMetadataResolver(
                catalog,
                durationEstimator);
            var host = new BrokerHost(
                queue,
                $"broker-{process.Id}",
                executionRouter.ExecuteAsync,
                scheduler: new ModelAwareScheduler(),
                scheduleMetadata: async (candidates, cancellationToken) =>
                {
                    var prepared = await executionRouter.PrepareAsync(
                        candidates,
                        cancellationToken);
                    var residentModel = executionRouter.ResidentModel;
                    return candidates
                        .Select(candidate => scheduleMetadata.Resolve(
                            candidate,
                            prepared.TryGetValue(
                                candidate.Request.JobId,
                                out var selection)
                                ? selection.Model
                                : null,
                            residentModel))
                        .ToArray();
                },
                residentModel: () => executionRouter.ResidentModel,
                durationObserver: scheduleMetadata.Observe,
                idleUnload: executionRouter.UnloadResidentAsync);
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
