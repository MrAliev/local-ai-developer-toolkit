using System.Diagnostics;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Abstractions;

public sealed class SystemFileSystemProbe : IFileSystemProbe
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public string GetFullPath(string path) => Path.GetFullPath(path);

    public string ResolvePhysicalPath(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return info.FullName;
        }

        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? throw new IOException($"Could not resolve reparse point '{path}'.");
    }

    public FileMetadataSnapshot GetMetadata(string path)
    {
        if (!File.Exists(path))
        {
            return FileMetadataSnapshot.Absent(path);
        }

        var info = new FileInfo(path);
        string? version = null;
        try
        {
            version = FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return new FileMetadataSnapshot(
            info.FullName,
            true,
            info.Length,
            info.LastWriteTimeUtc,
            version);
    }
}
