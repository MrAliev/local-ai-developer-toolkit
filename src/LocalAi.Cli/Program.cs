using LocalAi.Cli;
using System.Text.Json;

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
    var commonDirectory = args.Length > 2
        ? args[2]
        : await new LocalAi.Repository.GitClient()
            .GetCommonDirectoryAsync(Environment.CurrentDirectory);
    var runtimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi");
    Console.WriteLine(RepoCommand.Status(commonDirectory, runtimeRoot).Message);
    return 0;
}

if (args is ["bootstrap", "--dry-run", ..])
{
    var runtimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi");
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
    var result = await CodeSearchSyncCommand.ExecuteAsync(root);
    Console.WriteLine(
        $"SYNCED repository={result.RepositoryId} generation={result.GenerationId} " +
        $"overlays={result.OverlaysBuilt}");
    return 0;
}

if (args is ["hook", var hookName, ..])
{
    var rootIndex = Array.IndexOf(args, "--root");
    var root = rootIndex >= 0 && rootIndex + 1 < args.Length
        ? args[rootIndex + 1]
        : Environment.CurrentDirectory;
    if (!Enum.TryParse<RepositoryHookEvent>(
            hookName.Replace("-", string.Empty),
            ignoreCase: true,
            out _))
    {
        Console.Error.WriteLine($"Unsupported LocalAi hook '{hookName}'.");
        return 2;
    }

    var result = await CodeSearchSyncCommand.ExecuteAsync(root);
    Console.Error.WriteLine(
        $"LocalAi index synchronized: generation={result.GenerationId}, " +
        $"overlays={result.OverlaysBuilt}.");
    return 0;
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
    var commonDirectory = await new LocalAi.Repository.GitClient()
        .GetCommonDirectoryAsync(root);
    var result = HookInstaller.Install(
        commonDirectory,
        launcherPath,
        ["run", "localai"]);
    Console.WriteLine($"Installed {result.Installed.Count} shared Git hooks.");
    return 0;
}

Console.Error.WriteLine(
    "Usage: localai native <operation> [--request file] | " +
    "localai repo status [git-common-dir] | localai bootstrap --dry-run | " +
    "localai sync [--root dir] | localai hooks install [--root dir]");
return 2;
