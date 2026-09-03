using CodeSearch.Core.Embedding;
using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;
using System.Text.Json;

// Before the guard below, because the guard's own failure path prints: an exit code and a
// mangled reason is worse than either alone.
ConsoleOutputText.UseUtf8();

// The language every command answers in, decided before any of them runs — and after the
// encoding, because the first thing this can produce is a sentence the console has to be able
// to write. Numbers stay invariant whatever the language is; only the words move.
OutputCulture.Apply();

// Every command runs under one guard. A broker or Git failure used to leave the runtime's own
// stack trace on the console and exit with 0xE0434352, which tells an operator nothing and tells
// a hook or scheduled task even less. Failures now name themselves and pick an exit code that
// distinguishes "try again later" from "this input will never work".
try
{
    return await RunAsync(args);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("localai: cancelled.");
    return 130;
}
catch (EmbeddingUnavailableException exception)
{
    Console.Error.WriteLine($"localai: {exception.Message}");
    return 75;
}
catch (RepositorySyncBusyException exception)
{
    // The same "try again later" family as an unreachable embedder: the other run is doing
    // this run's work, and a hook or script must be able to tell that from a real fault.
    Console.Error.WriteLine($"localai: {exception.Message}");
    return 75;
}
catch (EmbeddingChunkException exception)
{
    Console.Error.WriteLine($"localai: {exception.Message}");
    return 65;
}
catch (Exception exception)
{
    // Type names and the inner chain, not just the message: a released binary once printed
    // exactly "Dll was not found." — a bare DllNotFoundException's default message — and
    // that one anonymous line cost five reproduction attempts without finding the cause
    // (#139). An unexpected failure is precisely the one whose message cannot be trusted
    // to identify itself.
    Console.Error.WriteLine($"localai: {UnexpectedFailure.Describe(exception)}");
    return 70;
}

static async Task<int> RunAsync(string[] args)
{
    if (args is ["model", .. var modelArguments])
    {
        using var processLifetime = new CancellationTokenSource();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            processLifetime.Token);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            processLifetime.Cancel();
        };
        EventHandler exitHandler = (_, _) => processLifetime.Cancel();
        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;
        try
        {
            return await ModelCommand.ExecuteProductionAsync(
                modelArguments,
                Console.Out,
                cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
        }
    }

    if (args is ["native", var operation, ..])
    {
        var requestIndex = Array.IndexOf(args, "--request");
        var requestPath = requestIndex >= 0 && requestIndex + 1 < args.Length
            ? args[requestIndex + 1]
            : null;
        var response = await NativeCommand.ExecuteAsync(operation, requestPath);
        Console.WriteLine(response.GetRawText());
        return 0;
    }

    if (args is ["repo", "status", ..])
    {
        if (!RepoCommand.TryParseStatusArguments(
                args.AsSpan(2).ToArray(),
                out var target,
                out var parseError))
        {
            Console.Error.WriteLine(parseError);
            return 2;
        }

        string commonDirectory;
        if (target.ResolveThroughGit)
        {
            var directory = target.Path ?? Environment.CurrentDirectory;
            try
            {
                commonDirectory = await new LocalAi.Repository.GitClient()
                    .GetCommonDirectoryAsync(directory);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or DirectoryNotFoundException)
            {
                // Saying which directory was asked about matters more than it sounds: the
                // failure this replaces reported a configured repository as unconfigured, so
                // "there is no repository here" has to be distinguishable from "this one is
                // not set up yet".
                Console.Error.WriteLine(
                    $"'{directory}' is not inside a Git repository.");
                return 2;
            }
        }
        else
        {
            commonDirectory = target.Path!;
            if (!Directory.Exists(commonDirectory))
            {
                Console.Error.WriteLine(
                    $"'{commonDirectory}' is not a directory. Pass a Git common directory, " +
                    "or use --root with a directory inside the repository.");
                return 2;
            }
        }

        Console.WriteLine(
            RepoCommand.Status(
                commonDirectory,
                ModelResidencyPolicyStore.DefaultRuntimeRoot).Message);
        return 0;
    }

    if (args is ["doctor", ..])
    {
        var rootIndex = Array.IndexOf(args, "--root");
        return DoctorCommand.Execute(
            ModelResidencyPolicyStore.DefaultRuntimeRoot,
            rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : null,
            Console.Out);
    }

    if (args is ["policy", ..])
    {
        return PolicyCommand.Execute(args.AsSpan(1).ToArray());
    }

    if (args is ["update", ..])
    {
        return await UpdateCommand.ExecuteAsync(args.AsSpan(1).ToArray());
    }

    if (args is ["semantic", .. var semanticArguments])
    {
        return SemanticNavigationCommand.Execute(semanticArguments);
    }

    if (args is ["bootstrap", "--dry-run", ..])
    {
        var runtimeRoot = ModelResidencyPolicyStore.DefaultRuntimeRoot;
        var plan = BootstrapCommand.Plan(
            await new LocalAi.Repository.GitClient()
                .GetCommonDirectoryAsync(Environment.CurrentDirectory),
            runtimeRoot,
            AppContext.BaseDirectory);
        foreach (var change in plan.Changes)
        {
            Console.WriteLine(change);
        }

        return 0;
    }

    if (args is ["sync", ..])
    {
        var rootIndex = Array.IndexOf(args, "--root");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length
            ? args[rootIndex + 1]
            : Environment.CurrentDirectory;
        var limitIndex = Array.IndexOf(args, SyncRefusal.LimitFlag);
        int? inlineLimit = null;
        if (limitIndex >= 0)
        {
            // A bound asked for and not understood must not become no bound at all: this is the
            // enforcement point, and failing open here is the defect the flag exists to remove.
            if (limitIndex + 1 >= args.Length ||
                !int.TryParse(args[limitIndex + 1], out var parsedLimit) ||
                parsedLimit < 0)
            {
                Console.Error.WriteLine(
                    "localai: --max-inline-files needs a whole number of files, " +
                    "for example --max-inline-files 200.");
                return 64;
            }

            inlineLimit = parsedLimit;
        }
        var result = await CodeSearchSyncCommand.ExecuteAsync(
            root,
            includeOverlays: !args.Contains("--base-only", StringComparer.Ordinal),
            requireSemantics: args.Contains("--require-semantics", StringComparer.Ordinal),
            refuseInlineOverFiles: inlineLimit);
        if (result.RefusedFiles is { } refused)
        {
            // Exit code 0: the run did exactly what it was asked to do. A non-zero code would
            // read as a failed sync to every caller that checks one — the MCP tool prints
            // "sync failed with N", and a hook would mark the commit's refresh as broken.
            Console.WriteLine(SyncRefusal.Line(
                result.RepositoryId,
                refused,
                // Reached only when a limit was given: nothing refuses without one.
                inlineLimit ?? refused));
            return 0;
        }

        Console.WriteLine(
            $"SYNCED repository={result.RepositoryId} generation={result.GenerationId} " +
            $"overlays={result.OverlaysBuilt}" +
            (result.WorktreesSkipped > 0
                ? $" skipped={result.WorktreesSkipped}"
                : string.Empty));
        return 0;
    }

    if (args is ["hook", .. var hookArguments])
    {
        // Git hooks are the only caller of this command, so its failures are read from a hook
        // log or not at all. Both of them now name the events that exist instead of printing
        // the top-level usage, which used to omit this command entirely.
        if (hookArguments is not [var hookName, ..])
        {
            Console.Error.WriteLine(
                $"localai hook requires an event. Usage: {LocalAi.Cli.CliUsage.Hook}");
            return 2;
        }

        var rootIndex = Array.IndexOf(args, "--root");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length
            ? args[rootIndex + 1]
            : Environment.CurrentDirectory;
        if (!HookCommand.IsDispatchedEvent(hookName))
        {
            Console.Error.WriteLine(
                $"Unsupported LocalAi hook '{hookName}'. " +
                $"Supported events: {LocalAi.Cli.CliUsage.HookEvents}.");
            return 2;
        }

        var result = await CodeSearchSyncCommand.ExecuteAsync(root);
        Console.Error.WriteLine(
            $"LocalAi index synchronized: generation={result.GenerationId}, " +
            $"overlays={result.OverlaysBuilt}" +
            (result.WorktreesSkipped > 0
                ? $", skipped={result.WorktreesSkipped}"
                : string.Empty));
        return 0;
    }

    if (args is ["prune", ..])
    {
        var runtimeRoot = ModelResidencyPolicyStore.DefaultRuntimeRoot;
        var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
        var report = PruneCommand.Execute(runtimeRoot, dryRun, DateTimeOffset.UtcNow);
        foreach (var line in report.Lines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine(dryRun
            ? CliText.PruneWouldReclaim(PruneCommand.Megabytes(report.BytesReclaimed))
            : CliText.PruneReclaimed(PruneCommand.Megabytes(report.BytesReclaimed)));
        return 0;
    }

    if (args is ["telemetry", ..])
    {
        return await TelemetryCommand.ExecuteAsync(
            ModelResidencyPolicyStore.DefaultRuntimeRoot,
            Console.Out);
    }

    if (args is ["hooks", "install", ..])
    {
        var launcherPath = Environment.GetEnvironmentVariable(
            "LOCALAI_LAUNCHER_PATH");
        if (string.IsNullOrWhiteSpace(launcherPath))
        {
            Console.Error.WriteLine(
                "LocalAi hooks require LOCALAI_LAUNCHER_PATH. " +
                "Run this command through the stable LocalAi launcher.");
            return 2;
        }

        var rootIndex = Array.IndexOf(args, "--root");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length
            ? args[rootIndex + 1]
            : Environment.CurrentDirectory;
        var git = new LocalAi.Repository.GitClient();
        var commonDirectory = await git.GetCommonDirectoryAsync(root);
        var result = HookInstaller.Install(
            commonDirectory,
            launcherPath,
            ["run", "localai"],
            await git.GetConfigurationAsync(root, "core.hooksPath"),
            await git.GetWorkingTreeRootAsync(root));
        Console.WriteLine(
            $"Installed {result.Installed.Count} shared Git hooks in " +
            $"{result.HooksDirectory}.");
        if (result.InsideWorkingTree)
        {
            Console.WriteLine(
                "The repository points core.hooksPath into its working tree, so the " +
                "dispatchers were also added to .git/info/exclude.");
        }

        return 0;
    }

    Console.Error.WriteLine(CliUsage.Text);
    return 2;
}
