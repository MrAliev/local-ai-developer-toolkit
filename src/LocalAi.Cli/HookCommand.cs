using LocalAi.Repository;

namespace LocalAi.Cli;

public enum RepositoryHookEvent
{
    PostCommit,
    PostMerge,
    PostRewrite,
    PostCheckout,
    ReferenceTransaction
}

/// <summary>
/// Which strings `localai hook` acts on.
///
/// The list that decides is <see cref="GitHookLayout.Events"/> — the hooks this product actually
/// installs — rather than the enum above. The two had drifted: the enum grew a fifth value that
/// nothing installs, nothing dispatches and no message names, and `Enum.TryParse` accepts numerals
/// as well as names. So `localai hook reference-transaction` and `localai hook 3` both ran a full
/// synchronisation, retention sweep included, and exited 0.
///
/// That matters more than a wrong argument usually does: this command is invoked by Git,
/// unattended, and what it does deletes. A hook script with a typo in it got a sweep rather than a
/// refusal, and said nothing about it.
///
/// Matching the installed list also means the guard and the message it prints cannot disagree
/// again, because both now read the same source.
/// </summary>
public static class HookCommand
{
    public static bool IsDispatchedEvent(string? requested) =>
        !string.IsNullOrWhiteSpace(requested) &&
        GitHookLayout.Events.Contains(requested.Trim(), StringComparer.OrdinalIgnoreCase);
}
