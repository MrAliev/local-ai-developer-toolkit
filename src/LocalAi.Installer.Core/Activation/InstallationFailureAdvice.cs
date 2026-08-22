namespace LocalAi.Installer.Core.Activation;

/// <summary>
/// Turns a layout refusal into something the person in front of the wizard can act on.
///
/// The layout lease reports the condition it found — "the directory still inherits access
/// rules" — and that is the right message for the code that raised it. It is not an
/// instruction. Somebody whose installation refuses itself needs to know which directory is
/// meant, whether it holds anything worth keeping, and what to do next; without that the run
/// ends in a sentence that reads like a defect in the product.
///
/// The advice is deliberately different depending on what the root actually holds. A root
/// left behind by a failed first run holds no versions and can simply go. A root with
/// installed versions holds this machine's indexes as well, so telling anyone to delete it
/// would trade one unexplained failure for a much more expensive one.
/// </summary>
public static class InstallationFailureAdvice
{
    private const string LayoutMarker = "installation layout is unsafe";

    public static bool IsLayoutFailure(string? message) =>
        message is not null &&
        message.Contains(LayoutMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Advice for a layout refusal, or null when the message is about something else.
    /// </summary>
    public static string? ForLayoutFailure(
        string? message,
        string root,
        bool holdsInstalledVersions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!IsLayoutFailure(message))
        {
            return null;
        }

        return holdsInstalledVersions
            ? $"{root} holds installed LocalAi versions, so it must not simply be deleted — " +
                "it also holds this machine's repository indexes. Close every LocalAi process " +
                "(clients registering the MCP servers included) and run the installer again; " +
                "if it still refuses, report this message together with the directory's " +
                "security settings."
            : $"{root} exists but holds no installed version, which is what a run that failed " +
                "before installing anything leaves behind. Delete that directory and run this " +
                "installer again; a root created by the installation itself carries the " +
                "protected permissions this check requires.";
    }
}
