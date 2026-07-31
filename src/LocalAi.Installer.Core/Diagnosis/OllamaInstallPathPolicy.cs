namespace LocalAi.Installer.Core.Diagnosis;

internal sealed class OllamaInstallPathPolicy
{
    private readonly IPhysicalPathResolver _physicalPathResolver;
    private readonly IReadOnlyList<string> _approvedDirectories;

    public OllamaInstallPathPolicy(
        IPhysicalPathResolver physicalPathResolver,
        IEnumerable<string> approvedDirectories)
    {
        _physicalPathResolver = physicalPathResolver
            ?? throw new ArgumentNullException(nameof(physicalPathResolver));
        _approvedDirectories = Array.AsReadOnly(
            (approvedDirectories ??
             throw new ArgumentNullException(nameof(approvedDirectories)))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public IReadOnlyList<string> GetCandidateDirectories(
        UninstallEntrySnapshot entry) =>
        Array.AsReadOnly(
            GetEntryDirectories(entry)
                .Concat(_approvedDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

    public bool IsCandidatePhysicalPath(
        string candidate,
        string identityPath,
        IEnumerable<string> candidateDirectories)
    {
        try
        {
            var candidatePhysical =
                _physicalPathResolver.ResolvePhysicalPath(candidate);
            var identityPhysical =
                _physicalPathResolver.ResolvePhysicalPath(identityPath);
            if (!string.Equals(
                    Path.GetFullPath(candidatePhysical),
                    Path.GetFullPath(identityPhysical),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(identityPhysical),
                    "ollama.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var executableDirectory = Path.GetDirectoryName(identityPhysical);
            foreach (var candidateDirectory in candidateDirectories)
            {
                try
                {
                    var candidateDirectoryPhysical =
                        _physicalPathResolver.ResolvePhysicalPath(
                            candidateDirectory);
                    if (string.Equals(
                            Path.TrimEndingDirectorySeparator(
                                Path.GetFullPath(executableDirectory!)),
                            Path.TrimEndingDirectorySeparator(
                                Path.GetFullPath(candidateDirectoryPhysical)),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (IsPathFailure(exception))
                {
                }
            }
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
        }

        return false;
    }

    public bool IsApprovedOfficialPhysicalPath(string identityPath)
    {
        try
        {
            var identityPhysical =
                _physicalPathResolver.ResolvePhysicalPath(identityPath);
            if (!string.Equals(
                    Path.GetFileName(identityPhysical),
                    "ollama.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var executableDirectory = Path.GetDirectoryName(identityPhysical);
            foreach (var approvedDirectory in _approvedDirectories)
            {
                try
                {
                    var approvedPhysical =
                        _physicalPathResolver.ResolvePhysicalPath(
                            approvedDirectory);
                    if (string.Equals(
                            Path.TrimEndingDirectorySeparator(
                                Path.GetFullPath(executableDirectory!)),
                            Path.TrimEndingDirectorySeparator(
                                Path.GetFullPath(approvedPhysical)),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (IsPathFailure(exception))
                {
                }
            }
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
        }

        return false;
    }

    internal static IReadOnlyList<string> GetApprovedOfficialDirectories()
    {
        var directories = new[]
        {
            (
                Root: Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                Relative: Path.Combine("Programs", "Ollama")),
            (
                Root: Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                Relative: "Ollama"),
            (
                Root: Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86),
                Relative: "Ollama"),
        };
        return Array.AsReadOnly(
            directories
                .Where(item => !string.IsNullOrWhiteSpace(item.Root))
                .Select(item => Path.Combine(item.Root, item.Relative))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static IReadOnlyList<string> GetEntryDirectories(
        UninstallEntrySnapshot entry)
    {
        var directories = new List<string>();
        AddDirectory(directories, entry.InstallLocation);
        AddExecutableDirectory(
            directories,
            ParseDisplayIcon(entry.DisplayIcon));
        AddExecutableDirectory(
            directories,
            ParseExecutableFromCommand(entry.UninstallString));
        return Array.AsReadOnly(directories.ToArray());
    }

    private static void AddExecutableDirectory(
        ICollection<string> directories,
        string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            AddDirectory(
                directories,
                Path.GetDirectoryName(executablePath));
        }
    }

    private static void AddDirectory(
        ICollection<string> directories,
        string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(directory);
            if (!directories.Contains(
                    fullPath,
                    StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(fullPath);
            }
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
        }
    }

    private static string? ParseDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var path = displayIcon.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..], out _))
        {
            path = path[..comma].Trim().Trim('"');
        }

        return path;
    }

    private static string? ParseExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1
                ? trimmed[1..closingQuote]
                : null;
        }

        var firstSpace = trimmed.IndexOf(' ');
        return firstSpace < 0
            ? trimmed
            : trimmed[..firstSpace];
    }

    private static bool IsPathFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        System.ComponentModel.Win32Exception or ArgumentException or
        NotSupportedException;
}
