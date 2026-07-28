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

    public void Publish(BrokerProcessState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, LocalAiJson.Strict));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, _statePath, overwrite: true);
                    break;
                }
                catch (Exception error) when (
                    error is IOException or UnauthorizedAccessException &&
                    attempt < 6)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
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
