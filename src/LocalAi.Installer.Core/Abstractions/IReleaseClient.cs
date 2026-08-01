namespace LocalAi.Installer.Core.Abstractions;

public interface IReleaseClient
{
    Task<Stream> OpenPackageAsync(
        Uri approvedPackageUri,
        long maximumBytes,
        CancellationToken cancellationToken);
}
