namespace LocalAi.Cli;

internal static class CliUsage
{
    public const string ModelStatus = "localai model status";
    public const string ModelPull =
        "localai model pull --model <model> --catalog-version <version>";
    public const string ModelPreflight =
        "localai model preflight --model <model> --context <tokens> " +
        "--catalog-version <version>";

    public const string Text =
        "Usage: localai native <operation> [--request file] | " +
        ModelStatus + " | " + ModelPull + " | " + ModelPreflight + " | " +
        "localai repo status [git-common-dir] | localai bootstrap --dry-run | " +
        "localai sync [--root dir] [--base-only] | localai hooks install [--root dir]";
}
