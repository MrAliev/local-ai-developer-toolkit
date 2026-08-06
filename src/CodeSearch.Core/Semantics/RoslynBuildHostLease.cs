using System.Reflection;

namespace CodeSearch.Core.Semantics;

internal sealed class RoslynBuildHostLease : IDisposable
{
    internal const string BaseDirectoryDataName = "APP_CONTEXT_BASE_DIRECTORY";
    private const string ResourcePrefix = "LocalAi.RoslynBuildHost/";
    private const string TemporaryDirectoryPrefix = "localai-roslyn-buildhost-";
    private static readonly SemaphoreSlim LoadLock = new(1, 1);
    private int _disposed;

    private RoslynBuildHostLease(string rootPath)
    {
        RootPath = rootPath;
    }

    internal string RootPath { get; }

    internal static async Task<IDisposable> AcquireLoadLockAsync(
        CancellationToken cancellationToken)
    {
        await LoadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LockReleaser();
    }

    internal static RoslynBuildHostLease? CreateIfNeeded(
        bool forceExtraction = false)
    {
        var expected = Path.Combine(
            AppContext.BaseDirectory,
            "BuildHost-netcore",
            "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll");
        if (!forceExtraction && File.Exists(expected))
        {
            return null;
        }

        var assembly = typeof(RoslynBuildHostLease).Assembly;
        var resources = assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0)
        {
            throw new InvalidOperationException(
                "Embedded Roslyn MSBuild hosts are missing from CodeSearch.Core.");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            TemporaryDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var resource in resources)
            {
                ExtractResource(assembly, resource, root);
            }

            if (!File.Exists(Path.Combine(
                    root,
                    "BuildHost-netcore",
                    "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll")))
            {
                throw new InvalidOperationException(
                    "Embedded Roslyn .NET build host was not extracted.");
            }

            return new RoslynBuildHostLease(root);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            TryDelete(RootPath);
        }
    }

    private static void ExtractResource(
        Assembly assembly,
        string resourceName,
        string root)
    {
        var relative = resourceName[ResourcePrefix.Length..]
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) ||
            relative.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment is "" or "." or ".."))
        {
            throw new InvalidOperationException("Invalid embedded Roslyn build-host path.");
        }

        var destination = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(root) +
            Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid embedded Roslyn build-host path.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var source = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException(
                "Embedded Roslyn build-host resource could not be opened.");
        using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        source.CopyTo(target);
    }

    private static void TryDelete(string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var tempRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            var prefix = tempRoot + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullRoot).StartsWith(
                    TemporaryDirectoryPrefix,
                    StringComparison.Ordinal))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
        catch
        {
            // A Roslyn child process can briefly retain a file during shutdown. The OS
            // temporary-file policy will eventually reclaim an abandoned directory.
        }
    }

    private sealed class LockReleaser : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                LoadLock.Release();
            }
        }
    }
}
