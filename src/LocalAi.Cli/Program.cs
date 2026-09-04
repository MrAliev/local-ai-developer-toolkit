using CodeSearch.Core.Embedding;
using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;
using System.Globalization;
using System.Text.Json;

// Before the guard below, because the guard's own failure path prints: an exit code and a
// mangled reason is worse than either alone.
ConsoleOutputText.UseUtf8();

// The language every command answers in, decided before any of them runs — and after the
// encoding, because the first thing this can produce is a sentence the console has to be able
// to write. Numbers stay invariant whatever the language is; only the words move.
//
// Except when the reader is a program. `--json` promises the same bytes on every machine, and
// one decision here keeps that promise for the catalogue and for the framework's own
// exception messages alike. Not for `Win32Exception`, whose words come from the operating
// system: those paths earn a code and a sentence of our own instead.
var machineReadable = MachineOutput.Requested(args);
if (machineReadable)
{
    OutputCulture.Apply(MachineOutput.Language(args), CultureInfo.CurrentUICulture);
    OutputCulture.PinInvariantFormatting();
}
else
{
    OutputCulture.Apply();
}

// Every command runs under one guard. A broker or Git failure used to leave the runtime's own
// stack trace on the console and exit with 0xE0434352, which tells an operator nothing and tells
// a hook or scheduled task even less. Failures now name themselves and pick an exit code that
// distinguishes "try again later" from "this input will never work".
try
{
    return await RunAsync(args, machineReadable);
}
catch (OperationCanceledException)
{
    return Failed("run_cancelled", CliText.RunCancelled, 130);
}
catch (EmbeddingUnavailableException exception)
{
    return Failed("broker_unavailable", exception.Message, 75);
}
catch (RepositorySyncBusyException exception)
{
    // The same "try again later" family as an unreachable embedder: the other run is doing
    // this run's work, and a hook or script must be able to tell that from a real fault.
    return Failed("sync_busy", exception.Message, 75);
}
catch (EmbeddingChunkException exception)
{
    return Failed("chunk_rejected", exception.Message, 65);
}
catch (Exception exception)
{
    // Type names and the inner chain, not just the message: a released binary once printed
    // exactly "Dll was not found." — a bare DllNotFoundException's default message — and
    // that one anonymous line cost five reproduction attempts without finding the cause
    // (#139). An unexpected failure is precisely the one whose message cannot be trusted
    // to identify itself.
    return Failed("unexpected_failure", UnexpectedFailure.Describe(exception), 70);
}

// One refusal, two faces. The prose keeps the `localai:` prefix it has always had; the envelope
// goes to stdout, because a caller that asked for JSON should never have to read stderr to find
// out what happened.
/// <summary>
/// The interrupt handling `model` already installs, for the two commands that can wait minutes
/// on a queue and a model.
/// </summary>
static async Task<int> Interruptible(Func<CancellationToken, Task<int>> work)
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
        return await work(cancellation.Token);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        AppDomain.CurrentDomain.ProcessExit -= exitHandler;
    }
}

/// <summary>
/// What the answer came from, in the shape the MCP tools already use for the same attribute, so
/// provenance reads the same in both faces.
/// </summary>
static string PrimarySource(IReadOnlyList<string> sources, string whenEmpty) =>
    sources.Count switch
    {
        0 => whenEmpty,
        1 => Path.GetFullPath(sources[0]),
        _ => $"{Path.GetFullPath(sources[0])} (+{sources.Count - 1} more)",
    };

static int Refuse(string command, CommandRefusal refused, bool machine)
{
    if (machine)
    {
        Console.WriteLine(
            MachineOutput.Refusal(command, refused.Code, refused.Message));
    }
    else
    {
        Console.Error.WriteLine(refused.Message);
    }

    return 2;
}

int Failed(string code, string message, int exitCode)
{
    if (machineReadable)
    {
        Console.WriteLine(MachineOutput.Refusal(
            MachineOutput.Enveloped(args) ?? MachineOutput.Named(args),
            code,
            message));
    }
    else
    {
        Console.Error.WriteLine($"localai: {message}");
    }

    return exitCode;
}

static async Task<int> RunAsync(string[] args, bool machineReadable)
{
    if (MachineOutput.Requested(args))
    {
        if (MachineOutput.Enveloped(args) is null)
        {
            var named = MachineOutput.Named(args);
            if (named.Length == 0)
            {
                Console.WriteLine(MachineOutput.Refusal(
                    named,
                    "command_unknown",
                    CliUsage.Text));
                return 2;
            }

            Console.WriteLine(MachineOutput.Refusal(
                named,
                "json_not_supported",
                CliText.JsonNotSupported));
            return 2;
        }

        args = MachineOutput.Without(args);
    }

    // First, because it is the command that describes the rest.
    if (args is ["capabilities", ..])
    {
        if (CapabilitiesCommand.Refused(args, machineReadable) is { } refused)
        {
            return Refuse("capabilities", refused, machineReadable);
        }

        Console.WriteLine(MachineOutput.Answer("capabilities", MachineOutput.Capabilities()));
        return 0;
    }

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
        JsonElement response;
        try
        {
            response = await NativeCommand.ExecuteAsync(operation, requestPath);
        }
        catch (ArgumentException refusal)
        {
            Console.Error.WriteLine(refusal.Message);
            return 2;
        }

        Console.WriteLine(response.GetRawText());
        return 0;
    }

    // The first commands in this binary that can run for minutes, so they are also the first
    // that have to answer Ctrl+C — an interrupted triage would otherwise exit with no envelope
    // and no line saying why.
    if (args is ["ask", .. var askArguments])
    {
        if (!AskCommand.TryParse(askArguments, out var ask, out var askRefusal))
        {
            return Refuse("ask", askRefusal!, machineReadable);
        }

        return await Interruptible(token => LocalModelRun.ExecuteAsync(
            "ask",
            "ask:" + PrimarySource(ask!.Files, "prompt"),
            ask.Profile,
            (tasks, inner) => tasks.AskAsync(
                ask.Profile,
                ask.Prompt,
                ask.Files,
                ask.Model,
                inner),
            machineReadable,
            token));
    }

    if (args is ["translate", .. var translateArguments])
    {
        if (!TranslateCommand.TryParse(
                translateArguments,
                Console.IsInputRedirected,
                out var translate,
                out var translateRefusal))
        {
            return Refuse("translate", translateRefusal!, machineReadable);
        }

        var source = translate!.Text;
        if (translate.FromStandardInput)
        {
            source = await Console.In.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(source))
            {
                return Refuse(
                    "translate",
                    new CommandRefusal("source_missing", CliText.TranslateEmptyInput),
                    machineReadable);
            }
        }

        return await Interruptible(token => LocalModelRun.TranslateAsync(
            "translate:" + (translate.FromStandardInput ? "stdin" : "argument"),
            translate,
            source!,
            machineReadable,
            token));
    }

    if (args is ["read-image", .. var imageArguments])
    {
        if (!ReadImageCommand.TryParse(imageArguments, out var image, out var imageRefusal))
        {
            return Refuse("read-image", imageRefusal!, machineReadable);
        }

        return await Interruptible(token => LocalModelRun.ExecuteAsync(
            "read-image",
            "read-image:" + PrimarySource(image!.Images, "images"),
            image.Profile,
            (tasks, inner) => tasks.ReadImageAsync(
                image.Images,
                image.Question,
                image.Profile,
                image.Model,
                inner),
            machineReadable,
            token));
    }

    if (args is ["triage", .. var triageArguments])
    {
        if (!TriageCommand.TryParse(
                triageArguments,
                Console.IsInputRedirected,
                out var triage,
                out var triageRefusal))
        {
            return Refuse("triage", triageRefusal!, machineReadable);
        }

        string? piped = null;
        if (triage!.FromStandardInput)
        {
            piped = await Console.In.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(piped))
            {
                // A pipe that carried nothing is a different mistake from no pipe at all, and
                // only the sentence tells the reader which one they made.
                return Refuse(
                    "triage",
                    new CommandRefusal("source_missing", CliText.TriageEmptyInput),
                    machineReadable);
            }
        }

        return await Interruptible(token => LocalModelRun.ExecuteAsync(
            "triage",
            "triage:" + (triage.Path ?? "stdin"),
            LocalTaskProfile.LogTriage,
            (tasks, inner) => tasks.TriageLogAsync(
                triage.Path,
                piped,
                triage.Question,
                triage.Model,
                inner),
            machineReadable,
            token));
    }

    if (args is ["repo", "status", ..])
    {
        if (!RepoCommand.TryParseStatusArguments(
                args.AsSpan(2).ToArray(),
                out var target,
                out var refusal))
        {
            return Refuse("repo status", refusal!, machineReadable);
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
                // Not `not_a_git_repository`: this catch also takes any non-zero git exit
                // and a git that would not start, so the code says what was established rather
                // than the usual cause of it. It is also not NOT_CONFIGURED, which is the
                // opposite situation — a repository resolved, and LocalAi not knowing it (#94).
                return Refuse(
                    "repo status",
                    new CommandRefusal(
                        "repository_not_resolved",
                        CliText.RepositoryOutsideGit(directory)),
                    machineReadable);
            }
        }
        else
        {
            commonDirectory = target.Path!;
            if (!Directory.Exists(commonDirectory))
            {
                return Refuse(
                    "repo status",
                    new CommandRefusal(
                        "directory_missing",
                        CliText.RepositoryPathNotDirectory(commonDirectory)),
                    machineReadable);
            }
        }

        var status = RepoCommand.Status(
            commonDirectory,
            ModelResidencyPolicyStore.DefaultRuntimeRoot);
        Console.WriteLine(machineReadable
            ? MachineOutput.Answer("repo status", RepoCommand.MachineStatus(status))
            : status.Message);
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
        Console.WriteLine(CliText.BootstrapDryRun);
        Console.WriteLine();
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
                Console.Error.WriteLine(CliText.SyncInlineLimitInvalid);
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
            Console.Error.WriteLine(CliText.HookEventMissing(LocalAi.Cli.CliUsage.Hook));
            return 2;
        }

        var rootIndex = Array.IndexOf(args, "--root");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length
            ? args[rootIndex + 1]
            : Environment.CurrentDirectory;
        if (!HookCommand.IsDispatchedEvent(hookName))
        {
            Console.Error.WriteLine(CliText.HookEventUnknown(
                hookName,
                LocalAi.Cli.CliUsage.HookEvents));
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
            Console.Error.WriteLine(CliText.HooksLauncherRequired);
            return 2;
        }

        var rootIndex = Array.IndexOf(args, "--root");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length
            ? args[rootIndex + 1]
            : Environment.CurrentDirectory;
        var git = new LocalAi.Repository.GitClient();
        var commonDirectory = await git.GetCommonDirectoryAsync(root);
        HookInstallResult result;
        try
        {
            result = HookInstaller.Install(
                commonDirectory,
                launcherPath,
                ["run", "localai"],
                await git.GetConfigurationAsync(root, "core.hooksPath"),
                await git.GetWorkingTreeRootAsync(root));
        }
        catch (InvalidOperationException blocked)
        {
            // A collision between the reader's own hook and a backup of an earlier one is
            // theirs to resolve, and the message says how. Exit 70 framed it as a fault.
            Console.Error.WriteLine(blocked.Message);
            return 2;
        }

        Console.WriteLine(CliText.HooksInstalled(
            result.Installed.Count,
            result.HooksDirectory));
        if (result.Chained.Count > 0)
        {
            // Said at last. Installing moves any hook the reader wrote to `<hook>.pre-localai`
            // and calls it first, and until now the command did that silently — the result
            // carried the list and nothing read it. File names rather than the recorded paths:
            // `Chained` holds the path before the move, so printing it verbatim would name files
            // that no longer exist, in the directory the line above already named.
            Console.WriteLine(CliText.HooksChained(
                LocalAi.Repository.GitHookLayout.ChainedSuffix,
                string.Join(", ", result.Chained.Select(Path.GetFileName))));
        }

        if (result.InsideWorkingTree)
        {
            Console.WriteLine(CliText.HooksExcluded);
        }

        return 0;
    }

    Console.Error.WriteLine(CliUsage.Text);
    return 2;
}
