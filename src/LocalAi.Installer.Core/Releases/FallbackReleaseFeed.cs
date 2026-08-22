namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// Tries the anonymous path first and keeps the authenticated one for when it is needed.
///
/// The order is the point. A public repository is readable by anyone, so an installation
/// should not begin by asking for an account; a private one is readable by nobody without a
/// sign-in, so the GitHub CLI has to remain reachable. Deciding by trying, rather than by
/// asking the user which case they are in, means neither audience is made to care about the
/// other's problem.
///
/// The secondary feed is consulted only for a resolution failure — a 404 for a private
/// repository looks exactly like a 404 for a wrong tag from outside, so both are worth a
/// second attempt. A verification failure is never retried: a manifest that failed the
/// embedded key is not a transport problem, and asking a different transport for the same
/// document until one of them is believed is precisely the shape of an attack this design
/// exists to refuse.
/// </summary>
public sealed class FallbackReleaseFeed(IReleaseFeed primary, IReleaseFeed? secondary)
    : IReleaseFeed
{
    private readonly IReleaseFeed primary =
        primary ?? throw new ArgumentNullException(nameof(primary));

    /// <summary>
    /// What the primary feed said when the secondary one was reached for, or null while it
    /// has not failed. Surfaced so a run report can say why it took the longer path.
    /// </summary>
    public string? FallbackReason { get; private set; }

    public Task<string> ResolveTagAsync(
        string requestedTag,
        CancellationToken cancellationToken) =>
        AttemptAsync(feed => feed.ResolveTagAsync(requestedTag, cancellationToken));

    public Task<ResolvedRelease> ResolveAsync(
        string tag,
        string workingDirectory,
        CancellationToken cancellationToken) =>
        AttemptAsync(feed => feed.ResolveAsync(tag, workingDirectory, cancellationToken));

    public Task<string> DownloadPackageAsync(
        string tag,
        string workingDirectory,
        IProgress<long>? bytesDownloaded,
        CancellationToken cancellationToken) =>
        AttemptAsync(feed => feed.DownloadPackageAsync(
            tag,
            workingDirectory,
            bytesDownloaded,
            cancellationToken));

    private async Task<T> AttemptAsync<T>(Func<IReleaseFeed, Task<T>> operation)
    {
        try
        {
            return await operation(primary).ConfigureAwait(false);
        }
        catch (ReleaseResolutionException exception) when (
            secondary is not null &&
            exception.InnerException is not ReleaseVerificationException)
        {
            FallbackReason = exception.Message;
            try
            {
                return await operation(secondary).ConfigureAwait(false);
            }
            catch (ReleaseResolutionException fallbackException)
            {
                // Both messages, because on a machine where neither path works the useful
                // question is which of the two failures the user can act on, and only they
                // know whether the repository is supposed to be public.
                throw new ReleaseResolutionException(
                    $"{exception.Message} The GitHub CLI was tried as well: " +
                    fallbackException.Message,
                    fallbackException);
            }
        }
    }
}
