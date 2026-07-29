using System.Text.Json.Serialization;

namespace LocalAi.Launcher;

public sealed record VersionPointer(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version);

public sealed record ResolvedTool(
    string Version,
    string VersionDirectory,
    string ExecutablePath);
