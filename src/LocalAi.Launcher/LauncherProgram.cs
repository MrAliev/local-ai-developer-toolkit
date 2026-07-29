namespace LocalAi.Launcher;

public static class LauncherProgram
{
    private const string Usage =
        """
        Usage: localai-launcher run <tool> [arguments...]
               localai-launcher activate <version> [--stop-running]
        """;

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

        if (args.Length >= 2 &&
            string.Equals(args[0], "run", StringComparison.Ordinal))
        {
            try
            {
                var application = new LauncherApplication(binRoot, launcherPath);
                return await application.RunAsync(
                    args[1],
                    args.Skip(2).ToArray(),
                    cancellationToken);
            }
            catch (LauncherException exception)
            {
                await error.WriteLineAsync($"{exception.Code}: {exception.Message}");
                return 1;
            }
        }

        if (args.Length is 2 or 3 &&
            string.Equals(args[0], "activate", StringComparison.Ordinal) &&
            (args.Length == 2 ||
             string.Equals(args[2], "--stop-running", StringComparison.Ordinal)))
        {
            try
            {
                var activator = new VersionActivator(
                    binRoot,
                    new LocalAiProcessController(),
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(15));
                activator.Activate(args[1], stopRunning: args.Length == 3);
                return 0;
            }
            catch (LauncherException exception)
            {
                await error.WriteLineAsync($"{exception.Code}: {exception.Message}");
                return 1;
            }
        }

        await error.WriteLineAsync(Usage);
        return 2;
    }
}
