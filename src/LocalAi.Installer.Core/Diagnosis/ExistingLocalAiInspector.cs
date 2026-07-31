using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed partial class ExistingLocalAiInspector(IFileSystemProbe fileSystem)
{
    private static readonly JsonSerializerOptions StrictJson = CreateJsonOptions();
    private static readonly IReadOnlyList<string> RequiredFiles =
    [
        "localai.exe",
        "codesearch.exe",
        "codesearch-mcp.exe",
        "locallm-mcp.exe",
        "LocalAi.Broker.dll",
        "LocalAi.Contracts.dll",
    ];

    public ExistingLocalAiSnapshot Inspect(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        var binRoot = Path.Combine(localAppData, "LocalAi", "bin");
        var currentPath = Path.Combine(binRoot, "current.json");
        if (!fileSystem.FileExists(currentPath))
        {
            return new ExistingLocalAiSnapshot(
                ExistingLocalAiState.Absent,
                null,
                null,
                null);
        }

        string? detectedVersion = null;
        string? detectedVersionPath = null;
        try
        {
            var pointer = JsonSerializer.Deserialize<CurrentPointer>(
                fileSystem.ReadAllText(currentPath),
                StrictJson);
            if (pointer is null ||
                pointer.SchemaVersion != 1 ||
                string.IsNullOrWhiteSpace(pointer.Version) ||
                !SafeVersionName().IsMatch(pointer.Version))
            {
                return Unrecognized("The current-version pointer is invalid.");
            }

            detectedVersion = pointer.Version;
            var versionsRoot = fileSystem.GetFullPath(Path.Combine(binRoot, "versions"));
            var versionPath = fileSystem.GetFullPath(Path.Combine(versionsRoot, pointer.Version));
            detectedVersionPath = versionPath;
            EnsureBelow(versionPath, versionsRoot);
            if (!fileSystem.DirectoryExists(versionPath))
            {
                return Unrecognized(
                    $"Version '{pointer.Version}' is missing.",
                    detectedVersion,
                    detectedVersionPath);
            }

            var physicalBinRoot = fileSystem.ResolvePhysicalPath(binRoot);
            var physicalVersionsRoot = fileSystem.ResolvePhysicalPath(versionsRoot);
            EnsureBelow(physicalVersionsRoot, physicalBinRoot);
            var physicalVersionPath = fileSystem.ResolvePhysicalPath(versionPath);
            EnsureBelow(physicalVersionPath, physicalVersionsRoot);
            var physicalRequiredFiles = new List<(string Logical, string Physical)>();
            foreach (var requiredFile in RequiredFiles)
            {
                var requiredPath = Path.Combine(versionPath, requiredFile);
                if (!fileSystem.FileExists(requiredPath))
                {
                    return Unrecognized(
                        $"Version '{pointer.Version}' is missing '{requiredFile}'.",
                        detectedVersion,
                        detectedVersionPath);
                }

                var physicalRequiredPath =
                    fileSystem.ResolvePhysicalPath(requiredPath);
                EnsureBelow(
                    physicalRequiredPath,
                    physicalVersionPath);
                physicalRequiredFiles.Add((requiredPath, physicalRequiredPath));
            }

            var revalidatedBinRoot = fileSystem.ResolvePhysicalPath(binRoot);
            var revalidatedVersionsRoot =
                fileSystem.ResolvePhysicalPath(versionsRoot);
            var revalidatedVersionPath =
                fileSystem.ResolvePhysicalPath(versionPath);
            EnsureUnchanged(physicalBinRoot, revalidatedBinRoot, binRoot);
            EnsureUnchanged(
                physicalVersionsRoot,
                revalidatedVersionsRoot,
                versionsRoot);
            EnsureUnchanged(
                physicalVersionPath,
                revalidatedVersionPath,
                versionPath);
            EnsureBelow(revalidatedVersionsRoot, revalidatedBinRoot);
            EnsureBelow(revalidatedVersionPath, revalidatedVersionsRoot);
            foreach (var requiredFile in physicalRequiredFiles)
            {
                var revalidatedRequired =
                    fileSystem.ResolvePhysicalPath(requiredFile.Logical);
                EnsureUnchanged(
                    requiredFile.Physical,
                    revalidatedRequired,
                    requiredFile.Logical);
                EnsureBelow(revalidatedRequired, revalidatedVersionPath);
            }

            return new ExistingLocalAiSnapshot(
                ExistingLocalAiState.Compatible,
                pointer.Version,
                versionPath,
                null);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or
            UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return Unrecognized(
                exception.Message,
                detectedVersion,
                detectedVersionPath);
        }
    }

    private static void EnsureBelow(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, comparison))
        {
            throw new IOException(
                $"Resolved path '{path}' is outside the LocalAi versions directory.");
        }
    }

    private static void EnsureUnchanged(
        string before,
        string after,
        string logicalPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetFullPath(before),
                Path.GetFullPath(after),
                comparison))
        {
            throw new IOException(
                $"Physical path for '{logicalPath}' changed during inspection.");
        }
    }

    private static ExistingLocalAiSnapshot Unrecognized(
        string reason,
        string? version = null,
        string? versionPath = null) =>
        new(ExistingLocalAiState.Unrecognized, version, versionPath, reason);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.MakeReadOnly();
        return options;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$")]
    private static partial Regex SafeVersionName();

    private sealed record CurrentPointer(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("version")] string Version);
}
