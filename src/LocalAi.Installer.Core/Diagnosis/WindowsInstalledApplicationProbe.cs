using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class WindowsInstalledApplicationProbe
    : IInstalledApplicationProbe
{
    private readonly IUninstallEntrySource _entrySource;
    private readonly IExecutableIdentityProbe _executableIdentity;
    private readonly OllamaInstallPolicy _policy;

    public WindowsInstalledApplicationProbe()
        : this(
            new WindowsRegistryUninstallEntrySource(),
            new SystemExecutableIdentityProbe(),
            new SystemPhysicalPathResolver(),
            OllamaInstallPathPolicy.GetApprovedOfficialDirectories())
    {
    }

    public WindowsInstalledApplicationProbe(
        IUninstallEntrySource entrySource,
        IExecutableIdentityProbe executableIdentity,
        IPhysicalPathResolver physicalPathResolver,
        IEnumerable<string> approvedDirectories)
    {
        _entrySource = entrySource
            ?? throw new ArgumentNullException(nameof(entrySource));
        _executableIdentity = executableIdentity
            ?? throw new ArgumentNullException(nameof(executableIdentity));
        _policy = new OllamaInstallPolicy(
            physicalPathResolver,
            approvedDirectories);
    }

    public Task<InstalledApplicationMetadata?> FindOllamaAsync(
        CancellationToken cancellationToken)
    {
        foreach (var entry in _entrySource.ReadEntries(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detected = _policy.Match(entry, _executableIdentity);
            if (detected is not null)
            {
                return Task.FromResult<InstalledApplicationMetadata?>(detected);
            }
        }

        return Task.FromResult<InstalledApplicationMetadata?>(null);
    }
}
