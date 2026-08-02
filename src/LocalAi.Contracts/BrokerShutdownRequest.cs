using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// Asks one specific broker to finish what it is doing and exit.
///
/// Addressed to a process id *and* its start time on purpose. A request left behind by a
/// crashed stopper, or one racing a broker that has already been replaced, would otherwise
/// shut down a healthy broker that happens to have inherited the id.
/// </summary>
public sealed record BrokerShutdownRequest(
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc);

/// <summary>
/// The request lives next to <c>host.json</c> as a file, in the same style as the rest of the
/// runtime's coordination, because the broker already wakes once a second to publish its
/// heartbeat and can read it there without a watcher, a port or a protocol.
/// </summary>
public static class BrokerShutdownRequestStore
{
    public const string FileName = "shutdown.request";

    public static string PathFor(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        return Path.Combine(Path.GetFullPath(runtimeRoot), FileName);
    }

    public static void Write(string runtimeRoot, BrokerShutdownRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = PathFor(runtimeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(request));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Returns null for an absent, unreadable or malformed request. A shutdown that cannot be
    /// read is not a shutdown: the broker keeps serving and the caller falls back to stopping
    /// it the blunt way, which is strictly better than exiting on a corrupt file.
    /// </summary>
    public static BrokerShutdownRequest? Read(string runtimeRoot)
    {
        var path = PathFor(runtimeRoot);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var request = JsonSerializer.Deserialize<BrokerShutdownRequest>(
                File.ReadAllText(path));
            return request is { ProcessId: > 0 } ? request : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Delete(string runtimeRoot)
    {
        try
        {
            var path = PathFor(runtimeRoot);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
