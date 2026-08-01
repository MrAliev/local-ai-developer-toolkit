using System.Diagnostics;

namespace LocalAi.Installer.Core.Abstractions;

public interface IProcessFactory
{
    IRunningProcess Start(ProcessStartInfo startInfo);
}

public interface IRunningProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void KillTree();
}

public sealed class SystemProcessFactory : IProcessFactory
{
    public IRunningProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            return new RunningSystemProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class RunningSystemProcess(Process process) : IRunningProcess
    {
        public int Id => process.Id;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);

        public void KillTree() => process.Kill(entireProcessTree: true);
        public void Dispose() => process.Dispose();
    }
}
