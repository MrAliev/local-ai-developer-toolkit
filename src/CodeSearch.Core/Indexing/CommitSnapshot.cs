using System.Diagnostics;
using System.IO.Compression;

namespace CodeSearch.Core.Indexing;

public sealed class CommitSnapshot : IDisposable
{
    private readonly string _temporaryRoot;

    private CommitSnapshot(string temporaryRoot, string root)
    {
        _temporaryRoot = temporaryRoot;
        Root = root;
    }

    public string Root { get; }

    public static CommitSnapshot Create(string repositoryRoot, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "localai-snapshot-" + Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(temporaryRoot, "content");
        var archivePath = Path.Combine(temporaryRoot, "snapshot.zip");
        Directory.CreateDirectory(contentRoot);
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = Path.GetFullPath(repositoryRoot),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("archive");
            start.ArgumentList.Add("--format=zip");
            start.ArgumentList.Add($"--output={archivePath}");
            start.ArgumentList.Add(revision);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start git archive.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git archive exited with {process.ExitCode}: {stderr.Result.Trim()}");
            }

            ZipFile.ExtractToDirectory(archivePath, contentRoot);
            File.Delete(archivePath);
            return new CommitSnapshot(temporaryRoot, contentRoot);
        }
        catch
        {
            Directory.Delete(temporaryRoot, recursive: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }
}
