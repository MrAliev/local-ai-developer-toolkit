using System.Runtime.InteropServices;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// Makes <c>DllImport("hostfxr")</c> resolvable before MSBuildLocator needs it.
///
/// <c>MSBuildLocator.RegisterDefaults()</c> enumerates installed SDKs by P/Invoking into
/// hostfxr. A framework-dependent process has that module loaded before the first managed
/// instruction runs, so the import binds without anyone thinking about it. The released
/// localai.exe is a self-contained single-file publish, where the host components are
/// statically linked into the apphost: no module named hostfxr exists in the process, the
/// Locator's fallback heuristics lean on <c>Assembly.Location</c> — empty inside a bundle —
/// and the first P/Invoke dies as a bare DllNotFoundException (#188, the reproduced half
/// of #139).
///
/// The cure is the loader's own rule: a relative-name library load checks already-loaded
/// modules first. Loading the right hostfxr.dll once, by full path, makes every later
/// "hostfxr" import bind to it. On machines where the import already resolves, nothing is
/// loaded and nothing changes — that conservatism is deliberate, because the failure has
/// never reproduced on this side (#139 records five negative stands) and a fix must not
/// disturb the machines that work.
/// </summary>
internal static class HostFxrPreloader
{
    public static void EnsureLoaded(Action<string>? diagnostic = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Loading by simple name performs the same probe the failing DllImport would, and
        // additionally finds a module that is already in the process. Success means there
        // is nothing to fix on this machine.
        if (NativeLibrary.TryLoad("hostfxr", out _))
        {
            return;
        }

        foreach (var root in CandidateDotnetRoots(
                     Environment.GetEnvironmentVariable,
                     ExecutableResolver.Find("dotnet"),
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "dotnet")))
        {
            if (FindNewestHostFxr(root) is not { } library)
            {
                continue;
            }

            if (NativeLibrary.TryLoad(library, out _))
            {
                diagnostic?.Invoke($"hostfxr preloaded from '{library}'.");
                return;
            }
        }

        // Nothing found. SDK discovery will fail exactly as it does today — and since #174
        // that failure names its type and the missing library — but this line records that
        // the preload ran and came up empty, so the next report starts past it.
        diagnostic?.Invoke(
            "hostfxr was not preloaded: no host/fxr/<version>/hostfxr.dll " +
            "under any dotnet root.");
    }

    /// <summary>
    /// The dotnet installation roots worth probing, in the order the host itself would
    /// honour them: the architecture-specific DOTNET_ROOT_* override, then DOTNET_ROOT,
    /// then the installation that owns the dotnet executable on PATH, then the default
    /// machine-wide install location.
    /// </summary>
    internal static IEnumerable<string> CandidateDotnetRoots(
        Func<string, string?> environment,
        string? dotnetExecutable,
        string? defaultRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[]
                 {
                     environment(
                         "DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture
                             .ToString()
                             .ToUpperInvariant()),
                     environment("DOTNET_ROOT"),
                     dotnetExecutable is null
                         ? null
                         : Path.GetDirectoryName(dotnetExecutable),
                     defaultRoot,
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// The newest hostfxr.dll under <c>host/fxr</c> of the given installation, or null.
    /// Any hostfxr can enumerate every SDK beside it, so newest is always safe; a stable
    /// version outranks its own prerelease, matching how the versions themselves order.
    /// </summary>
    internal static string? FindNewestHostFxr(string dotnetRoot)
    {
        var fxrRoot = Path.Combine(dotnetRoot, "host", "fxr");
        if (!Directory.Exists(fxrRoot))
        {
            return null;
        }

        string? bestPath = null;
        Version? bestVersion = null;
        var bestIsStable = false;
        var bestSuffix = string.Empty;
        foreach (var directory in Directory.EnumerateDirectories(fxrRoot))
        {
            var library = Path.Combine(directory, "hostfxr.dll");
            if (!File.Exists(library))
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            var separator = name.IndexOf('-');
            if (!Version.TryParse(separator < 0 ? name : name[..separator], out var version))
            {
                continue;
            }

            var isStable = separator < 0;
            var suffix = separator < 0 ? string.Empty : name[(separator + 1)..];
            var better =
                bestVersion is null ||
                version > bestVersion ||
                (version == bestVersion &&
                 ((isStable && !bestIsStable) ||
                  (isStable == bestIsStable &&
                   string.CompareOrdinal(suffix, bestSuffix) > 0)));
            if (better)
            {
                bestPath = library;
                bestVersion = version;
                bestIsStable = isStable;
                bestSuffix = suffix;
            }
        }

        return bestPath;
    }
}
