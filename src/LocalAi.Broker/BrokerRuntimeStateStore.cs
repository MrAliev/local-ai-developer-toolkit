using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class BrokerRuntimeStateStore
{
    private readonly string _statePath;

    public BrokerRuntimeStateStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var root = Path.GetFullPath(runtimeRoot);
        Directory.CreateDirectory(root);
        _statePath = Path.Combine(root, "host.json");
    }

    /// <summary>
    /// How long the rename keeps retrying while another process holds the state file open.
    ///
    /// Contention here is brief and ordinary — a reader has host.json open for the moment it
    /// takes to parse it — so the budget only has to outlast a read, not a workload.
    /// </summary>
    public static TimeSpan DefaultRetryBudget { get; } = TimeSpan.FromSeconds(2);

    public void Publish(BrokerProcessState state) => Publish(state, DefaultRetryBudget);

    /// <param name="retryBudget">
    /// A duration rather than a number of attempts. Six attempts with a rising sleep came to
    /// about 375ms of patience, which is ample on an idle machine and not on a loaded one: the
    /// test that proves the retry works released its lock after 75ms and still lost the race on
    /// a CI runner. An attempt count silently means different amounts of waiting depending on
    /// what else the machine is doing; a budget means what it says.
    /// </param>
    public void Publish(BrokerProcessState state, TimeSpan retryBudget)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryBudget, TimeSpan.Zero);
        var temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, LocalAiJson.Strict));
            var deadline = DateTime.UtcNow + retryBudget;
            while (true)
            {
                try
                {
                    File.Move(temporaryPath, _statePath, overwrite: true);
                    break;
                }
                catch (Exception error) when (
                    error is IOException or UnauthorizedAccessException &&
                    DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(25));
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void DeleteIfOwnedBy(BrokerProcessState owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        BrokerProcessState? current;
        try
        {
            current = File.Exists(_statePath)
                ? JsonSerializer.Deserialize<BrokerProcessState>(
                    File.ReadAllText(_statePath),
                    LocalAiJson.Strict)
                : null;
        }
        catch (JsonException)
        {
            return;
        }

        if (current is not null &&
            current.ProcessId == owner.ProcessId &&
            current.StartedAtUtc == owner.StartedAtUtc)
        {
            File.Delete(_statePath);
        }
    }
}
