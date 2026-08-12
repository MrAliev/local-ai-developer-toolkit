namespace LocalAi.Cli;

internal static class CliUsage
{
    public const string ModelStatus = "localai model status";
    public const string ModelPull =
        "localai model pull --model <model> --catalog-version <version>";
    public const string ModelPreflight =
        "localai model preflight --model <model> --context <tokens> " +
        "--catalog-version <version>";

    public const string Hook =
        "localai hook <" + HookEvents + "> [--root dir]";
    public const string HookEvents = "post-commit|post-checkout|post-merge|post-rewrite";
    public const string Prune = "localai prune [--dry-run]";
    public const string Doctor = "localai doctor [--root dir]";
    public const string Telemetry = "localai telemetry";

    // Every command the binary actually answers to belongs here. The hook command was missing
    // from this list while every installed Git hook invoked it, so anyone debugging a hook was
    // told by the tool itself that the command it had just run did not exist.
    public const string Text =
        "Usage: localai native <operation> [--request file] | " +
        ModelStatus + " | " + ModelPull + " | " + ModelPreflight + " | " +
        "localai repo status [git-common-dir] | localai policy <show|set> [options] | " +
        "localai semantic <operation> [--request file] | localai bootstrap --dry-run | " +
        "localai sync [--root dir] [--base-only] | " + Hook + " | " +
        "localai hooks install [--root dir] | " + Prune + " | " + Doctor + " | " +
        Telemetry;
}
