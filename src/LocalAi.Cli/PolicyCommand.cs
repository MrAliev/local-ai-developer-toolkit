using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>
/// Reads and changes the installation-wide broker policy.
///
/// Relaxing residency is a real trade, not a tuning knob, so the command prints what the
/// choice costs instead of silently accepting it, and warns that a running broker keeps the
/// old policy until it is restarted.
/// </summary>
internal static class PolicyCommand
{
    public static int Execute(string[] args)
    {
        var store = new ModelResidencyPolicyStore(
            ModelResidencyPolicyStore.DefaultRuntimeRoot);

        return args switch
        {
            ["show", ..] or [] => Show(store),
            ["set", "--residency", var value, ..] => Set(store, value),
            ["set", "--idle-model-keep-alive-seconds", var value, ..] =>
                SetIdleModelKeepAlive(store, value),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            Usage: localai policy show
                   localai policy set --residency <RequireFullVram|AllowPartialOffload|AllowCpu>
                   localai policy set --idle-model-keep-alive-seconds <non-negative integer>

              RequireFullVram      Models must sit entirely in video memory. Default.
              AllowPartialOffload  Part of a model may spill to system memory. Requires an
                                   adapter that holds at least some of it. Slower.
              AllowCpu             Models may run entirely on the CPU. Works without a usable
                                   adapter; substantially slower.
              Idle keep-alive      Seconds to retain an idle model when no queued job targets
                                   it. Zero unloads it immediately and is the default.
            """);
        return 2;
    }

    private static int Show(ModelResidencyPolicyStore store)
    {
        var policy = store.Read();
        Console.WriteLine($"model residency: {policy.ModelResidency}");
        Console.WriteLine(
            $"idle model keep-alive: {policy.IdleModelKeepAliveSeconds} seconds");
        if (policy.ModelResidency != ModelResidencyPolicy.RequireFullVram)
        {
            Console.WriteLine(
                "warning: residency is relaxed; responses may be substantially slower " +
                "than a fully resident load.");
        }

        return 0;
    }

    private static int SetIdleModelKeepAlive(ModelResidencyPolicyStore store, string value)
    {
        if (!int.TryParse(value, out var seconds) || seconds < 0)
        {
            Console.Error.WriteLine(
                $"Invalid idle model keep-alive '{value}'; expected a non-negative integer.");
            return Usage();
        }

        store.Write(store.Read() with { IdleModelKeepAliveSeconds = seconds });
        Console.WriteLine($"idle model keep-alive: {seconds} seconds");
        Console.WriteLine(
            "note: a broker that is already running keeps the previous policy until it is " +
            "restarted.");
        return 0;
    }

    private static int Set(ModelResidencyPolicyStore store, string value)
    {
        if (!Enum.TryParse<ModelResidencyPolicy>(value, ignoreCase: false, out var residency) ||
            !Enum.IsDefined(residency))
        {
            Console.Error.WriteLine($"Unknown residency policy '{value}'.");
            return Usage();
        }

        store.Write(store.Read() with { ModelResidency = residency });
        Console.WriteLine($"model residency: {residency}");
        if (residency != ModelResidencyPolicy.RequireFullVram)
        {
            Console.WriteLine(
                residency == ModelResidencyPolicy.AllowCpu
                    ? "warning: models may now run entirely on the CPU. Expect a large " +
                        "slowdown; every degraded answer carries a warning."
                    : "warning: models may now be partially offloaded to system memory. " +
                        "Expect a slowdown; every degraded answer carries a warning.");
        }

        Console.WriteLine(
            "note: a broker that is already running keeps the previous policy until it is " +
            "restarted.");
        return 0;
    }
}
