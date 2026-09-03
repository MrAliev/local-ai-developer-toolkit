using System.Globalization;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Cli;

/// <summary>
/// Reads and changes the installation-wide broker policy.
///
/// Relaxing residency is a real trade, not a tuning knob, so the command prints what the
/// choice costs instead of silently accepting it, and warns that a running broker keeps the
/// old policy until it is restarted.
///
/// What it says follows the reader; what it asks to be typed does not. Field names, option
/// names, enum values and the tokens `on` and `off` are the same in every language, because
/// they are what comes back on the next command line.
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
        error.WriteLine(CliText.PolicyUsage(
            UpdateCheckPolicy.MinimumIntervalHours,
            UpdateCheckPolicy.MaximumIntervalHours));
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
            error.WriteLine(CliText.PolicyLanguageUnknown(
                value,
                string.Join(", ", OutputCulture.Supported)));
            return Usage(error);
        }

        languages.Write(following ? null : wanted.ToLowerInvariant());
        output.WriteLine(Describe(languages));
        output.WriteLine(CliText.PolicyLanguageRestartNote);
        return 0;
    }

    /// <summary>
    /// What the language setting is, in the one form both `show` and `set` print, so the two
    /// cannot describe the same file differently.
    ///
    /// A stored choice renders as the code that was stored, which is the code that would be
    /// typed to store it again; only the sentence explaining `system` is prose.
    /// </summary>
    private static string Describe(OutputLanguageStore languages) =>
        languages.Read() is { } chosen
            ? $"language: {chosen}"
            : CliText.PolicyLanguageSystem;

    private static int Show(
        ModelResidencyPolicyStore store,
        UpdateCheckPolicyStore updates,
        UpdateCheckStateStore states,
        OutputLanguageStore languages,
        TextWriter output)
    {
        var policy = store.Read();
        output.WriteLine($"model residency: {policy.ModelResidency}");
        output.WriteLine(CliText.PolicyKeepAlive(policy.IdleModelKeepAliveSeconds));
        if (policy.ModelResidency != ModelResidencyPolicy.RequireFullVram)
        {
            output.WriteLine(CliText.PolicyResidencyRelaxed);
            output.WriteLine(CliText.PolicyResidencyMarks);
        }

        var updatePolicy = updates.Read();
        output.WriteLine(updatePolicy.Enabled
            ? CliText.PolicyUpdateCheckOn(updatePolicy.IntervalHours)
            : "update check: off");
        output.WriteLine(Describe(states.Read()));
        output.WriteLine(Describe(languages));
        return 0;
    }

    /// <summary>
    /// What the last check learned, in the same words every other surface uses. Read from the
    /// state file, never from the network: `policy show` is a question about this machine.
    ///
    /// The three clauses are the doctor report's own, reused rather than paraphrased — the
    /// promise this method's summary makes, which two near-copies did not keep. The field name
    /// is composed here rather than inside each clause so it stays one string in one place.
    ///
    /// `Verified` gets its own clause because nothing here compares the release against what is
    /// installed; a `policy show` that answered "up to date" would be answering a question it
    /// never asked.
    /// </summary>
    private static string Describe(UpdateCheckState state) =>
        "latest verified release: " + state.Status switch
        {
            UpdateCheckStatus.Verified => CliText.UpdateVerifiedRelease(
                state.LatestVersion,
                state.CheckedAtUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            UpdateCheckStatus.Unavailable => CliText.UpdateUnknownUnavailable(
                state.CheckedAtUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            _ => CliText.UpdateNeverChecked,
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
                error.WriteLine(CliText.PolicyUpdateCheckUnknown(value));
                return Usage(error);
        }

        var policy = updates.Read() with { Enabled = enabled };
        updates.Write(policy);
        output.WriteLine(enabled
            ? CliText.PolicyUpdateCheckOn(policy.IntervalHours)
            : "update check: off");
        // Printed on the way in, not buried in a document: consent to a network call means
        // knowing what the call is — and knowing it in the language the rest of this terminal
        // answers in, which is why the paragraph is picked rather than printed.
        output.WriteLine(enabled
            ? OutputCulture.Pick(UpdateCheckPolicy.Disclosure, UpdateCheckPolicy.DisclosureRussian)
            : CliText.PolicyUpdateCheckNothingFetched);
        output.WriteLine(CliText.PolicyRestartNote);
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
            error.WriteLine(CliText.PolicyUpdateCheckIntervalInvalid(
                value,
                UpdateCheckPolicy.MinimumIntervalHours,
                UpdateCheckPolicy.MaximumIntervalHours));
            return Usage(error);
        }

        var policy = updates.Read() with { IntervalHours = hours };
        updates.Write(policy);
        output.WriteLine(policy.Enabled
            ? CliText.PolicyUpdateCheckOn(hours)
            : CliText.PolicyUpdateCheckOffWithInterval(hours));
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
            error.WriteLine(CliText.PolicyKeepAliveInvalid(value));
            return Usage(error);
        }

        store.Write(store.Read() with { IdleModelKeepAliveSeconds = seconds });
        output.WriteLine(CliText.PolicyKeepAlive(seconds));
        output.WriteLine(CliText.PolicyRestartNote);
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
            error.WriteLine(CliText.PolicyResidencyUnknown(value));
            return Usage(error);
        }

        store.Write(store.Read() with { ModelResidency = residency });
        output.WriteLine($"model residency: {residency}");
        if (residency != ModelResidencyPolicy.RequireFullVram)
        {
            output.WriteLine(residency == ModelResidencyPolicy.AllowCpu
                ? CliText.PolicyResidencyNowCpu
                : CliText.PolicyResidencyNowPartial);
        }

        output.WriteLine(CliText.PolicyRestartNote);
        return 0;
    }
}
