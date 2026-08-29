namespace LocalAi.Installer.Core.Dependencies;

/// <summary>
/// A winget the installer is willing to run, or the reason it will not run one.
/// </summary>
public sealed record TrustedWingetResult(
    bool Allowed,
    string ExecutablePath,
    ExecutableTrustStatus Status,
    string Message);

/// <summary>
/// Hands out the winget executable to run, and refuses to hand out anything else.
///
/// The environment detector finds winget by walking a search path and returning the first file
/// called <c>winget.exe</c>. That answers "is there a winget" and nothing more: the search path
/// is writable by the user, and the wizard uses the answer to run installs machine-wide. A file
/// that happens to be named winget is not winget.
///
/// Verification therefore happens here rather than at detection, and on every call rather than
/// once. Detection runs on the first page and the last dependency is installed several pages and
/// several minutes later — checking once would only prove what was true then. That is what
/// <see cref="IWinGetExecutableTrust.Revalidate"/> exists for, and it is why the path handed back
/// is the canonical one the check resolved, never the one the detector reported.
///
/// Refusal is closed: no path is returned and the caller cannot run anything. Saying "could not
/// verify, carrying on" would leave the check as decoration.
/// </summary>
public sealed class TrustedWingetSource
{
    private readonly IWinGetExecutableTrust trust;
    private TrustedExecutable? verified;

    public TrustedWingetSource(IWinGetExecutableTrust trust)
    {
        this.trust = trust ?? throw new ArgumentNullException(nameof(trust));
    }

    /// <summary>
    /// The winget to run right now, having just been checked. Call this immediately before each
    /// invocation; the result is not worth keeping.
    /// </summary>
    /// <param name="detectedPath">
    /// What the environment detector reported. Used only to find the executable the first time,
    /// and never trusted: what is run is what the check resolves.
    /// </param>
    public TrustedWingetResult Authorize(string? detectedPath)
    {
        var result = verified is null
            ? trust.Resolve(detectedPath ?? string.Empty)
            : trust.Revalidate(verified);

        if (result.Status != ExecutableTrustStatus.Trusted || result.Executable is null)
        {
            // Dropped rather than kept, so a later call re-resolves from scratch instead of
            // revalidating against something already found wanting.
            verified = null;
            return new TrustedWingetResult(
                false,
                string.Empty,
                result.Status,
                Explain(result.Status));
        }

        verified = result.Executable;
        return new TrustedWingetResult(
            true,
            result.Executable.CanonicalPath,
            ExecutableTrustStatus.Trusted,
            string.Empty);
    }

    /// <summary>
    /// What to tell someone whose winget was refused. Each one names what was found and what to
    /// do about it: "verification failed" on its own leaves a person with no move to make.
    /// </summary>
    internal static string Explain(ExecutableTrustStatus status) => status switch
    {
        ExecutableTrustStatus.InvalidPath =>
            "The winget on this machine is not the one Windows ships. It has to be the App " +
            "Installer's, under Program Files\\WindowsApps or the WindowsApps alias. Install " +
            "App Installer from the Microsoft Store, then run this installer again.",
        ExecutableTrustStatus.Unavailable =>
            "winget could not be inspected on this machine, so it will not be run. Install App " +
            "Installer from the Microsoft Store, then run this installer again.",
        ExecutableTrustStatus.UntrustedPublisher =>
            "The winget on this machine is not signed by Microsoft Corporation. It will not be " +
            "run. Reinstall App Installer from the Microsoft Store.",
        ExecutableTrustStatus.UntrustedAcl =>
            "The winget on this machine sits where a non-administrator can write to it, so it " +
            "could have been replaced since Windows installed it. It will not be run. " +
            "Reinstall App Installer from the Microsoft Store.",
        ExecutableTrustStatus.Changed =>
            "winget changed on disk while this installer was running, so what was checked is " +
            "not what would be run. Close this installer and start it again.",
        ExecutableTrustStatus.UnsupportedPlatform =>
            "winget can only be verified on Windows.",
        _ =>
            "winget could not be verified on this machine, so it will not be run.",
    };
}
