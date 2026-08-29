using CodeSearch.Core.Embedding;
using LocalAi.Cli;
using LocalAi.Contracts;
using System.Text.Json;

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
        var result = await CodeSearchSyncCommand.ExecuteAsync(
            root,
            includeOverlays: !args.Contains("--base-only", StringComparer.Ordinal),
            requireSemantics: args.Contains("--require-semantics", StringComparer.Ordinal));
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
        if (!Enum.TryParse<RepositoryHookEvent>(
                hookName.Replace("-", string.Empty),
                ignoreCase: true,
                out _))
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
                ? $", skipped worktrees={result.WorktreesSkipped}"
                : string.Empty) +
            ".");
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

        Console.WriteLine(
            (dryRun ? "WOULD RECLAIM " : "RECLAIMED ") +
            PruneCommand.Megabytes(report.BytesReclaimed));
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
