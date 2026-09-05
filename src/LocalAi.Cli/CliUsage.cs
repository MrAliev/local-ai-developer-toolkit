namespace LocalAi.Cli;

internal static class CliUsage
{
    public const string ModelStatus = "localai model status";
    public const string ModelPull =
        "localai model pull --model <model> --catalog-version <version> "  +
        "[--progress-json]";
    public const string ModelPreflight =
        "localai model preflight --model <model> --context <tokens> " +
        "--catalog-version <version>";

    public const string Hook =
        "localai hook <" + HookEvents + "> [--root dir]";
    public const string HookEvents = "post-commit|post-checkout|post-merge|post-rewrite";
    public const string RepoStatus =
        "localai repo status [--root dir | git-common-dir] [--json]";
    public const string Prune = "localai prune [--dry-run]";
    public const string Doctor = "localai doctor [--root dir] [--json]";
    public const string Telemetry = "localai telemetry";
    public const string Ask =
        "localai ask <prompt> [file ...] [--profile name] [--model model] [--json]";
    public const string Triage =
        "localai triage [log-file|-] [--question text] [--model model] [--json]";
    // The three ways in are one slot and only one may be filled, which is the fact worth showing;
    // `repo status` above already spells an alternation this way. `--in -` is not advertised
    // here — the bare `-` in the same bracket already stands for standard input.
    public const string Translate =
        "localai translate [text|-|--in file] --from language --to language [--markdown] " +
        "[--out file] [--json]";
    public const string ReadImage =
        "localai read-image <question> <image> [image ...] [--profile name] "  +
        "[--model model] [--json]";
    // No brackets around the flag: here it is required rather than optional. The listing is
    // for a program, and the block this line sits in is the list for a person.
    public const string Capabilities = "localai capabilities --json";

    // Every command the binary actually answers to belongs here. The hook command was missing
    // from this list while every installed Git hook invoked it, so anyone debugging a hook was
    // told by the tool itself that the command it had just run did not exist.
    public const string Text =
        "Usage: localai native <operation> [--request file] | " +
        ModelStatus + " | " + ModelPull + " | " + ModelPreflight + " | " +
        RepoStatus + " | " +
        "localai policy <show|set> [options] | " +
        "localai update [--wait] [--force] | " +
        "localai semantic <operation> [options] | localai bootstrap --dry-run | " +
        "localai sync [--root dir] [--base-only] [--require-semantics] [--max-inline-files n] | " + Hook + " | " +
        "localai hooks install [--root dir] | " + Prune + " | " + Doctor + " | " +
        Telemetry + " | " + Ask + " | " + Triage + " | " + ReadImage + " | " + Translate +
        " | " + Capabilities;
}
