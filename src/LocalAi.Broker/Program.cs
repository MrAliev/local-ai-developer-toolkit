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
            // Advice, not an event: repeating it once per retry turn would bury the diagnostics
            // it is meant to explain.
            var backendHintPrinted = false;

            // Published on the heartbeat so a client can say why its job never ran. The broker's
            // own stderr goes nowhere a user looks: it is started detached, with no console.
            var backendReachable = new BackendReachability(ollamaUri);
            var backendStarter = new BackendStarter(
                ollamaUri,
                new OllamaLaunchRecordStore(runtimeRoot),
                message => Console.Error.WriteLine("LocalAi broker: " + message));
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
                durationObserver: (request, receipt, duration) =>
                {
                    // A finished job is the only proof the backend answered; failures are
                    // what the diagnostic carries, and successes are silent.
                    backendReachable.Answered();
                    scheduleMetadata.Observe(request, receipt, duration);
                },
                idleUnload: executionRouter.UnloadResidentAsync,
                idleUnloadAfter: TimeSpan.FromSeconds(policy.IdleModelKeepAliveSeconds),
                backendProbe: transport.ProbeActiveModelAsync,
                diagnostic: diagnostic =>
                {
                    Console.Error.WriteLine(
                        $"LocalAi broker diagnostic: job={diagnostic.JobId:N} " +
                        $"operation={diagnostic.Operation} " +
                        $"exception={diagnostic.ExceptionType}.");
                    backendHintPrinted = ReportUnreachableBackend(
                        diagnostic,
                        ollamaUri,
                        backendHintPrinted);
                    backendReachable.Observe(diagnostic);
                    backendStarter.OnDiagnostic(diagnostic);
                });
            // Set by the heartbeat loop, read by the host loop between jobs. A stopper asks for
            // this instead of killing the process, so the job in flight is finished and
            // reported rather than abandoned half way through an inference.
            var drain = new DrainSignal();
            var heartbeat = PublishHeartbeatAsync(
                stateStore,
                owner,
                runtimeRoot,
                drain,
                backendReachable,
                shutdown.Token);
            try
            {
                await host.RunAsync(shutdown.Token, () => drain.Requested);
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
        catch (Exception exception)
        {
            // Last line of defence. An HttpRequestException from a backend that was not up yet
            // used to travel out of the host loop and off the top of the process, which Windows
            // reports as a crashed application rather than as a broker that could not work. The
            // scheduling path no longer throws it, but a broker that dies must still say why and
            // leave a non-zero code behind for whoever started it.
            Console.Error.WriteLine(
                $"LocalAi broker stopped: {exception.GetType().Name}: {exception.Message}");
            return 70;
        }
        finally
        {
            stateStore.DeleteIfOwnedBy(owner);
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// Names the endpoint when the backend is what failed. The diagnostic line above carries an
    /// exception type, which does not tell an operator that starting Ollama is the fix.
    /// </summary>
    private static bool ReportUnreachableBackend(
        BrokerHostDiagnostic diagnostic,
        Uri ollamaUri,
        bool alreadyPrinted)
    {
        if (alreadyPrinted ||
            !string.Equals(
                diagnostic.ExceptionType,
                nameof(HttpRequestException),
                StringComparison.Ordinal))
        {
            return alreadyPrinted;
        }

        Console.Error.WriteLine(
            $"LocalAi broker: Ollama is not reachable at {ollamaUri}. Queued work is kept and " +
            "waits for it to answer; start Ollama if it is not running.");
        return true;
    }

    /// <summary>
    /// Whether the backend answered, as last seen by the host loop.
    ///
    /// Written from the diagnostic callback and read by the heartbeat, both on the broker's own
    /// threads, so a volatile flag is the whole of it -- this is a fact to publish, not a
    /// decision to coordinate.
    /// </summary>
    private sealed class BackendReachability(Uri endpoint)
    {
        private volatile bool _unreachable;

        public BrokerBackendState Current => new(!_unreachable, endpoint.ToString());

        public void Observe(BrokerHostDiagnostic diagnostic)
        {
            if (string.Equals(
                    diagnostic.ExceptionType,
                    nameof(HttpRequestException),
                    StringComparison.Ordinal))
            {
                _unreachable = true;
            }
        }

        public void Answered() => _unreachable = false;
    }

    private sealed class DrainSignal
    {
        private volatile bool requested;

        public bool Requested => requested;

        public void Request() => requested = true;
    }

    /// <summary>
    /// Publishes the heartbeat and, on the same tick, notices a shutdown request addressed to
    /// this broker. The loop already runs once a second, so noticing costs one file check and
    /// no watcher, port or protocol.
    ///
    /// The request is deleted as soon as it is accepted: leaving it behind would shut down the
    /// broker that replaces this one, seconds after it starts.
    /// </summary>
    private static async Task PublishHeartbeatAsync(
        BrokerRuntimeStateStore store,
        BrokerProcessState owner,
        string runtimeRoot,
        DrainSignal drain,
        BackendReachability backend,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            store.Publish(owner with
            {
                HeartbeatAtUtc = DateTimeOffset.UtcNow,
                Backend = backend.Current,
            });
            if (drain.Requested)
            {
                continue;
            }

            var request = BrokerShutdownRequestStore.Read(runtimeRoot);
            if (request is not null &&
                request.ProcessId == owner.ProcessId &&
                request.StartedAtUtc == owner.StartedAtUtc)
            {
                BrokerShutdownRequestStore.Delete(runtimeRoot);
                drain.Request();
            }
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
