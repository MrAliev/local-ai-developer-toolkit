namespace LocalAi.Launcher;

public static class LauncherProgram
{
    private const string Usage = "Usage: localai-launcher run <tool> [arguments...]";

    public static async Task<int> RunAsync(
        string[] args,
        string binRoot,
        string launcherPath,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length < 2 || !string.Equals(args[0], "run", StringComparison.Ordinal))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        try
        {
            var application = new LauncherApplication(binRoot, launcherPath);
            return await application.RunAsync(args[1], args.Skip(2).ToArray(), cancellationToken);
        }
        catch (LauncherException exception)
        {
            await error.WriteLineAsync($"{exception.Code}: {exception.Message}");
            return 1;
        }
    }
}
