using LocalAi.Contracts.Activation;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LocalAi.Launcher;

public sealed class VersionResolver
{
    private static readonly JsonSerializerOptions StrictJson = CreateJsonOptions();
    private readonly string _binRoot;
    private readonly string _versionsRoot;
    private readonly StringComparison _pathComparison;
    private readonly Func<string, string> _resolvePhysicalPath;

    public VersionResolver(string binRoot)
        : this(binRoot, ResolvePhysicalPath)
    {
    }

    public VersionResolver(
        string binRoot,
        Func<string, string> resolvePhysicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        _resolvePhysicalPath = resolvePhysicalPath
            ?? throw new ArgumentNullException(nameof(resolvePhysicalPath));
        _binRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binRoot));
        _versionsRoot = Path.Combine(_binRoot, "versions");
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public ResolvedTool Resolve(string tool)
    {
        if (!LauncherLayout.Tools.TryGetValue(tool, out var executableName))
        {
            throw new LauncherException(
                "tool_not_allowed",
                $"LocalAi tool '{tool}' is not allowlisted.");
        }

        var pointer = ReadPointer();
        var versionDirectory = ValidateVersion(pointer.Version);
        return new ResolvedTool(
            pointer.Version,
            versionDirectory,
            Path.Combine(versionDirectory, executableName));
    }

    public VersionPointer ReadCurrent() => ReadPointer();

    public string ValidateVersion(string version)
    {
        ValidateVersionName(version);
        var versionDirectory = Path.GetFullPath(Path.Combine(_versionsRoot, version));
        EnsureBelow(versionDirectory, _versionsRoot, "version_path_invalid");
        if (!Directory.Exists(versionDirectory))
        {
            throw new LauncherException(
                "version_incomplete",
                $"LocalAi version '{version}' does not exist.");
        }

        var physicalVersionsRoot = _resolvePhysicalPath(_versionsRoot);
        var physicalVersionDirectory = _resolvePhysicalPath(versionDirectory);
        EnsureBelow(
            physicalVersionDirectory,
            physicalVersionsRoot,
            "version_path_invalid");
        foreach (var fileName in LauncherLayout.RequiredFiles)
        {
            var requiredPath = Path.Combine(versionDirectory, fileName);
            if (!File.Exists(requiredPath))
            {
                throw new LauncherException(
                    "version_incomplete",
                    $"LocalAi version '{version}' is missing '{fileName}'.");
            }

            EnsureBelow(
                _resolvePhysicalPath(requiredPath),
                physicalVersionDirectory,
                "version_path_invalid");
        }

        return versionDirectory;
    }

    private VersionPointer ReadPointer()
    {
        var path = Path.Combine(_binRoot, "current.json");
        if (!File.Exists(path))
        {
            throw new LauncherException(
                "current_pointer_missing",
                $"LocalAi current-version pointer is missing: {path}");
        }

        try
        {
            var pointer = JsonSerializer.Deserialize<VersionPointer>(
                File.ReadAllText(path),
                StrictJson);
            if (pointer is null ||
                pointer.SchemaVersion != 1 ||
                string.IsNullOrWhiteSpace(pointer.Version))
            {
                throw new LauncherException(
                    "current_pointer_invalid",
                    "LocalAi current-version pointer is invalid.");
            }

            return pointer;
        }
        catch (JsonException exception)
        {
            throw new LauncherException(
                "current_pointer_invalid",
                $"LocalAi current-version pointer is invalid: {exception.Message}");
        }
    }

    internal static void ValidateVersionName(string? version)
    {
        if (!LocalAiVersionName.IsSafe(version))
        {
            throw new LauncherException(
                "version_path_invalid",
                "The LocalAi version name is invalid.");
        }
    }

    private void EnsureBelow(string path, string root, string code)
    {
        var prefix = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, _pathComparison))
        {
            throw new LauncherException(
                code,
                $"Resolved path is outside the LocalAi versions directory: {path}");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.MakeReadOnly();
        return options;
    }

    private static string ResolvePhysicalPath(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return info.FullName;
        }

        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new LauncherException(
                    "version_path_invalid",
                    $"Could not resolve reparse point '{path}'.");
        }
        catch (IOException exception)
        {
            throw new LauncherException(
                "version_path_invalid",
                $"Could not resolve reparse point '{path}': {exception.Message}");
        }
    }
}
