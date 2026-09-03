using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Cli;

/// <summary>
/// Reads and changes the installation-wide broker policy.
///
/// Relaxing residency is a real trade, not a tuning knob, so the command prints what the
/// choice costs instead of silently accepting it, and warns that a running broker keeps the
/// old policy until it is restarted.
/// </summary>
public static class PolicyCommand
{
    public static int Execute(string[] args) =>
        Execute(args, ModelResidencyPolicyStore.DefaultRuntimeRoot, Console.Out, Console.Error);

    /// <summary>
    /// The command with its runtime root and its output named, so a test can run it against a
    /// directory of its own instead of the machine's — and read what it printed rather than
    /// trusting that it printed anything.
    /// </summary>
    public static int Execute(
        string[] args,
        string runtimeRoot,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var store = new ModelResidencyPolicyStore(runtimeRoot);
        var updates = new UpdateCheckPolicyStore(runtimeRoot);
        var languages = new OutputLanguageStore(runtimeRoot);

        return args switch
        {
            ["show", ..] or [] =>
                Show(store, updates, new UpdateCheckStateStore(runtimeRoot), languages, output),
            ["set", "--language", var value, ..] => SetLanguage(languages, value, output, error),
            ["set", "--residency", var value, ..] => Set(store, value, output, error),
            ["set", "--idle-model-keep-alive-seconds", var value, ..] =>
                SetIdleModelKeepAlive(store, value, output, error),
            ["set", "--update-check", var value, ..] =>
                SetUpdateCheck(updates, value, output, error),
            ["set", "--update-check-interval-hours", var value, ..] =>
                SetUpdateCheckInterval(updates, value, output, error),
            _ => Usage(error),
        };
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine(
            """
            Usage: localai policy show
                   localai policy set --residency <RequireFullVram|AllowPartialOffload|AllowCpu>
                   localai policy set --idle-model-keep-alive-seconds <non-negative integer>
                   localai policy set --update-check <on|off>
                   localai policy set --update-check-interval-hours <1..720>
                   localai policy set --language <en|ru|system>

              RequireFullVram      Models must sit entirely in video memory. Default.
              AllowPartialOffload  Part of a model may spill to system memory. Requires an
                                   adapter that holds at least some of it. Slower.
              AllowCpu             Models may run entirely on the CPU. Works without a usable
                                   adapter; substantially slower.
              Idle keep-alive      Seconds to retain an idle model when no queued job targets
                                   it. Zero unloads it immediately and is the default.
              Update check         Whether this installation may look up whether a newer
                                   release exists. Off by default; nothing is ever downloaded
                                   or installed without you asking.
              Language             What the CLI and the MCP tools answer in. Follows this
                                   computer by default, and falls back to English where there
                                   is no translation. Commands and option names never change.
            """);
        return 2;
    }

    /// <summary>
    /// Stores the language every process that prints will answer in, or clears the choice so
    /// they follow the machine again.
    ///
    /// A language with no resources behind it is refused here rather than stored and quietly
    /// ignored later: a setting that reads back as something else is worse than an error.
    /// </summary>
    private static int SetLanguage(
        OutputLanguageStore languages,
        string value,
        TextWriter output,
        TextWriter error)
    {
        var wanted = value.Trim();
        var following = wanted.Equals("system", StringComparison.OrdinalIgnoreCase) ||
            wanted.Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (!following &&
            !OutputCulture.Supported.Contains(wanted, StringComparer.OrdinalIgnoreCase))
        {
            error.WriteLine(
                $"Unknown language '{value}'. This installation speaks " +
                string.Join(", ", OutputCulture.Supported) +
                ", or 'system' to follow the operating system.");
            return Usage(error);
        }

        languages.Write(following ? null : wanted.ToLowerInvariant());
        output.WriteLine(Describe(languages));
        output.WriteLine(
            "note: a broker, an MCP server or a shell that is already running keeps the " +
            "previous language until it is restarted.");
        return 0;
    }

    /// <summary>
    /// What the language setting is, in the one form both `show` and `set` print, so the two
    /// cannot describe the same file differently.
    /// </summary>
    private static string Describe(OutputLanguageStore languages) =>
        languages.Read() is { } chosen
            ? $"language: {chosen}"
            : "language: system — the language this computer is set to, English where there is " +
                "no translation";

    private static int Show(
        ModelResidencyPolicyStore store,
        UpdateCheckPolicyStore updates,
        UpdateCheckStateStore states,
        OutputLanguageStore languages,
        TextWriter output)
    {
        var policy = store.Read();
        output.WriteLine($"model residency: {policy.ModelResidency}");
        output.WriteLine(
            $"idle model keep-alive: {policy.IdleModelKeepAliveSeconds} seconds");
        if (policy.ModelResidency != ModelResidencyPolicy.RequireFullVram)
        {
            output.WriteLine(
                "warning: residency is relaxed; responses may be substantially slower " +
                "than a fully resident load.");
            output.WriteLine(
                "warning: answers from the LocalLm tools are labelled as degraded in the line " +
                "they print; embedding and search do not route through that line and carry no " +
                "mark. Restore strict residency with: " +
                "localai policy set --residency RequireFullVram");
        }

        var updatePolicy = updates.Read();
        output.WriteLine(updatePolicy.Enabled
            ? $"update check: on, every {updatePolicy.IntervalHours} hours"
            : "update check: off");
        output.WriteLine(Describe(states.Read()));
        output.WriteLine(Describe(languages));
        return 0;
    }

    /// <summary>
    /// What the last check learned, in the same words every other surface uses. Read from the
    /// state file, never from the network: `policy show` is a question about this machine.
    /// </summary>
    private static string Describe(UpdateCheckState state) => state.Status switch
    {
        UpdateCheckStatus.Verified =>
            $"latest verified release: {state.LatestVersion} " +
            $"(checked {state.CheckedAtUtc:yyyy-MM-dd HH:mm} UTC)",
        UpdateCheckStatus.Unavailable =>
            "latest verified release: unknown — the last check produced nothing to believe " +
            $"(tried {state.CheckedAtUtc:yyyy-MM-dd HH:mm} UTC)",
        _ => "latest verified release: unknown — nothing has been checked yet",
    };

    private static int SetUpdateCheck(
        UpdateCheckPolicyStore updates,
        string value,
        TextWriter output,
        TextWriter error)
    {
        bool enabled;
        switch (value.ToLowerInvariant())
        {
            case "on" or "true" or "yes" or "enabled":
                enabled = true;
                break;
            case "off" or "false" or "no" or "disabled":
                enabled = false;
                break;
            default:
                error.WriteLine($"Unknown update check setting '{value}'.");
                return Usage(error);
        }

        var policy = updates.Read() with { Enabled = enabled };
        updates.Write(policy);
        output.WriteLine(enabled
            ? $"update check: on, every {policy.IntervalHours} hours"
            : "update check: off");
        // Printed on the way in, not buried in a document: consent to a network call means
        // knowing what the call is.
        output.WriteLine(enabled
            ? UpdateCheckPolicy.Disclosure
            : "No release information will be fetched. Existing results, if any, stay on " +
                "disk until the next check replaces them.");
        output.WriteLine(
            "note: a broker that is already running keeps the previous policy until it is " +
            "restarted.");
        return 0;
    }

    private static int SetUpdateCheckInterval(
        UpdateCheckPolicyStore updates,
        string value,
        TextWriter output,
        TextWriter error)
    {
        if (!int.TryParse(value, out var hours) ||
            hours < UpdateCheckPolicy.MinimumIntervalHours ||
            hours > UpdateCheckPolicy.MaximumIntervalHours)
        {
            error.WriteLine(
                $"Invalid update check interval '{value}'; expected " +
                $"{UpdateCheckPolicy.MinimumIntervalHours} to " +
                $"{UpdateCheckPolicy.MaximumIntervalHours} hours.");
            return Usage(error);
        }

        var policy = updates.Read() with { IntervalHours = hours };
        updates.Write(policy);
        output.WriteLine(policy.Enabled
            ? $"update check: on, every {hours} hours"
            : $"update check: off; interval set to {hours} hours for when it is turned on");
        return 0;
    }

    private static int SetIdleModelKeepAlive(
        ModelResidencyPolicyStore store,
        string value,
        TextWriter output,
        TextWriter error)
    {
        if (!int.TryParse(value, out var seconds) || seconds < 0)
        {
            error.WriteLine(
                $"Invalid idle model keep-alive '{value}'; expected a non-negative integer.");
            return Usage(error);
        }

        store.Write(store.Read() with { IdleModelKeepAliveSeconds = seconds });
        output.WriteLine($"idle model keep-alive: {seconds} seconds");
        output.WriteLine(
            "note: a broker that is already running keeps the previous policy until it is " +
            "restarted.");
        return 0;
    }

    private static int Set(
        ModelResidencyPolicyStore store,
        string value,
        TextWriter output,
        TextWriter error)
    {
        if (!Enum.TryParse<ModelResidencyPolicy>(value, ignoreCase: false, out var residency) ||
            !Enum.IsDefined(residency))
        {
            error.WriteLine($"Unknown residency policy '{value}'.");
            return Usage(error);
        }

        store.Write(store.Read() with { ModelResidency = residency });
        output.WriteLine($"model residency: {residency}");
        if (residency != ModelResidencyPolicy.RequireFullVram)
        {
            output.WriteLine(
                residency == ModelResidencyPolicy.AllowCpu
                    ? "warning: models may now run entirely on the CPU. Expect a large " +
                        "slowdown; answers from the LocalLm tools are labelled as degraded " +
                        "in the line they print."
                    : "warning: models may now be partially offloaded to system memory. " +
                        "Expect a slowdown; answers from the LocalLm tools are labelled as " +
                        "degraded in the line they print.");
        }

        output.WriteLine(
            "note: a broker that is already running keeps the previous policy until it is " +
            "restarted.");
        return 0;
    }
}
