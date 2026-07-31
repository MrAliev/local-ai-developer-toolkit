using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Abstractions;

public interface IEnvironmentProbe
{
    HostEnvironmentSnapshot GetHost();
    string LocalAppData { get; }
    string UserProfile { get; }
    string? ResolveExecutable(string executableName);
}

public interface IFileSystemProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string ReadAllText(string path);
    string GetFullPath(string path);
    string ResolvePhysicalPath(string path);
    FileMetadataSnapshot GetMetadata(string path);
}

public interface IInstalledApplicationProbe
{
    Task<InstalledApplicationMetadata?> FindOllamaAsync(
        CancellationToken cancellationToken);
}

public interface IDiskProbe
{
    DiskSnapshot Observe(string path);
}

public interface INetworkProbe
{
    Task<NetworkSnapshot> ObserveAsync(CancellationToken cancellationToken);
}

public interface IWindowsGpuProbe
{
    Task<GpuSnapshot> ProbeAsync(CancellationToken cancellationToken);
}
