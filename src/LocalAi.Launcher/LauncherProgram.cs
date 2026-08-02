using LocalAi.Contracts.Activation;

namespace LocalAi.Launcher;

public static class LauncherProgram
{
    private const string Usage =
        """
        Usage: localai-launcher run <tool> [arguments...]
               localai-launcher activate <version> (--if-current-missing | --if-current-sha256 <SHA256>) [--stop-running]
               localai-launcher stop [--version <version>]
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

        if (TryParseStop(args, out var stopVersion))
        {
            try
            {
                // Deliberately does not touch the pointer. Replacing the stable launcher needs
                // the running tools gone and nothing else; folding that into activation is what
                // made the two impossible to order correctly.
                var stopper = new VersionStopper(
                    binRoot,
                    new LocalAiProcessController(),
                    TimeSpan.FromSeconds(15));
                stopper.Stop(stopVersion);
                return 0;
            }
            catch (LauncherException exception)
            {
                await error.WriteLineAsync($"{exception.Code}: {exception.Message}");
                return 1;
            }
        }

        if (TryParseActivation(args, out var activation))
        {
            try
            {
                var activator = new VersionActivator(
                    binRoot,
                    new LocalAiProcessController(),
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(15));
                activator.Activate(
                    activation.Version,
                    activation.StopRunning,
                    activation.Expectation);
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

    /// <summary>
    /// `stop` on its own means the active version. An explicit `--version` is for the case
    /// where the pointer has already moved on and something is still running out of the old
    /// directory.
    /// </summary>
    private static bool TryParseStop(IReadOnlyList<string> args, out string? version)
    {
        version = null;
        if (args.Count == 0 || !string.Equals(args[0], "stop", StringComparison.Ordinal))
        {
            return false;
        }

        if (args.Count == 1)
        {
            return true;
        }

        if (args.Count != 3 ||
            !string.Equals(args[1], "--version", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            return false;
        }

        version = args[2];
        return true;
    }

    private static bool TryParseActivation(
        IReadOnlyList<string> args,
        out ActivationArguments activation)
    {
        activation = default!;
        if (args.Count < 3 ||
            !string.Equals(args[0], "activate", StringComparison.Ordinal))
        {
            return false;
        }

        CurrentPointerExpectation? expectation = null;
        var stopRunning = false;
        for (var index = 2; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--stop-running", StringComparison.Ordinal))
            {
                if (stopRunning)
                {
                    return false;
                }

                stopRunning = true;
                continue;
            }

            if (string.Equals(args[index], "--if-current-missing", StringComparison.Ordinal))
            {
                if (expectation is not null)
                {
                    return false;
                }

                expectation = CurrentPointerExpectation.Missing;
                continue;
            }

            if (string.Equals(args[index], "--if-current-sha256", StringComparison.Ordinal))
            {
                if (expectation is not null || ++index >= args.Count ||
                    args[index].Length != 64 ||
                    args[index].Any(character =>
                        character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                {
                    return false;
                }

                expectation = CurrentPointerExpectation.ExactSha256(
                    Convert.FromHexString(args[index]));
                continue;
            }

            return false;
        }

        if (expectation is null)
        {
            return false;
        }

        activation = new(args[1], stopRunning, expectation);
        return true;
    }

    private sealed record ActivationArguments(
        string Version,
        bool StopRunning,
        CurrentPointerExpectation Expectation);
}
