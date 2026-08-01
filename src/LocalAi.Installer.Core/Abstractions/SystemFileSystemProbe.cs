using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LocalAi.Installer.Core.Diagnosis;
using Microsoft.Win32.SafeHandles;

namespace LocalAi.Installer.Core.Abstractions;

public sealed class SystemFileSystemProbe : IFileSystemProbe
{
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public string GetFullPath(string path) => Path.GetFullPath(path);

    public string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo info = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? info.FullName;
        }

        using var handle = CreateFileW(
            fullPath,
            0,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open '{fullPath}' for physical-path resolution.");
        }

        var requiredLength = GetFinalPathNameByHandleW(
            handle,
            null,
            0,
            0);
        if (requiredLength == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not resolve physical path for '{fullPath}'.");
        }

        var buffer = new StringBuilder(checked((int)requiredLength + 1));
        var written = GetFinalPathNameByHandleW(
            handle,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (written == 0 || written >= buffer.Capacity)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not resolve physical path for '{fullPath}'.");
        }

        return Path.GetFullPath(NormalizeExtendedPath(buffer.ToString()));
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
        catch (Win32Exception)
        {
        }

        return new FileMetadataSnapshot(
            info.FullName,
            true,
            info.Length,
            info.LastWriteTimeUtc,
            version);
    }

    private static string NormalizeExtendedPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder? filePath,
        uint filePathLength,
        uint flags);
}
