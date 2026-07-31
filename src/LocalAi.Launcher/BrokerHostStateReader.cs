using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LocalAi.Launcher;

internal sealed class BrokerHostStateReader
{
    private static readonly JsonSerializerOptions StrictJson = CreateJsonOptions();
    private readonly TimeProvider _timeProvider;

    internal BrokerHostStateReader(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal BrokerHostOwnership? ReadFreshOwnership(string runtimeRoot)
    {
        var path = Path.Combine(runtimeRoot, "host.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<BrokerHostState>(
                File.ReadAllText(path),
                StrictJson);
            return IsTrustedOwnership(state)
                ? new BrokerHostOwnership(
                    state!.ProcessId,
                    state.StartedAtUtc,
                    state.BrokerAssemblyPath!)
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool IsTrustedOwnership(BrokerHostState? state) =>
        state is
        {
            ProcessId: > 0,
            BrokerAssemblyPath: { Length: > 0 }
        } &&
        state.StartedAtUtc != default &&
        !string.IsNullOrWhiteSpace(state.BrokerAssemblyPath) &&
        _timeProvider.GetUtcNow() - state.HeartbeatAtUtc <= TimeSpan.FromSeconds(5) &&
        (state.SchemaVersion == 2 && state.Compatibility is null ||
         state.SchemaVersion == 3 && state.Compatibility is
         {
             ProtocolVersion: > 0,
             BuildCompatibilityId: { Length: > 0 }
         } && !string.IsNullOrWhiteSpace(state.Compatibility.BuildCompatibilityId));

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
}

internal sealed record BrokerHostOwnership(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string BrokerAssemblyPath);

internal sealed record BrokerHostState(
    [property: JsonRequired] int ProcessId,
    [property: JsonRequired] DateTimeOffset StartedAtUtc,
    [property: JsonRequired] DateTimeOffset HeartbeatAtUtc,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string? BrokerAssemblyPath,
    BrokerHostCompatibility? Compatibility = null);

internal sealed record BrokerHostCompatibility(
    [property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] string? BuildCompatibilityId);
