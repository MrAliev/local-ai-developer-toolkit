using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelStatusCommandSuccess(
    [property: JsonRequired, JsonPropertyName("schemaVersion"), JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonRequired, JsonPropertyName("operation"), JsonPropertyOrder(1)] string Operation,
    [property: JsonRequired, JsonPropertyName("accepted"), JsonPropertyOrder(2)] bool Accepted,
    [property: JsonRequired, JsonPropertyName("catalogVersion"), JsonPropertyOrder(3)] string CatalogVersion,
    [property: JsonRequired, JsonPropertyName("installedModels"), JsonPropertyOrder(4)] IReadOnlyList<string> InstalledModels,
    [property: JsonRequired, JsonPropertyName("pendingPullModels"), JsonPropertyOrder(5)] IReadOnlyList<string> PendingPullModels);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelPullCommandSuccess(
    [property: JsonRequired, JsonPropertyName("schemaVersion"), JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonRequired, JsonPropertyName("operation"), JsonPropertyOrder(1)] string Operation,
    [property: JsonRequired, JsonPropertyName("accepted"), JsonPropertyOrder(2)] bool Accepted,
    [property: JsonRequired, JsonPropertyName("model"), JsonPropertyOrder(3)] string Model,
    [property: JsonRequired, JsonPropertyName("catalogVersion"), JsonPropertyOrder(4)] string CatalogVersion,
    [property: JsonRequired, JsonPropertyName("status"), JsonPropertyOrder(5)] string Status);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelPreflightCommandSuccess(
    [property: JsonRequired, JsonPropertyName("schemaVersion"), JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonRequired, JsonPropertyName("operation"), JsonPropertyOrder(1)] string Operation,
    [property: JsonRequired, JsonPropertyName("accepted"), JsonPropertyOrder(2)] bool Accepted,
    [property: JsonRequired, JsonPropertyName("model"), JsonPropertyOrder(3)] string Model,
    [property: JsonRequired, JsonPropertyName("contextTokens"), JsonPropertyOrder(4)] int ContextTokens,
    [property: JsonRequired, JsonPropertyName("catalogVersion"), JsonPropertyOrder(5)] string CatalogVersion,
    [property: JsonRequired, JsonPropertyName("sizeBytes"), JsonPropertyOrder(6)] long SizeBytes,
    [property: JsonRequired, JsonPropertyName("sizeVramBytes"), JsonPropertyOrder(7)] long SizeVramBytes,
    [property: JsonRequired, JsonPropertyName("fullyResident"), JsonPropertyOrder(8)] bool FullyResident,
    [property: JsonRequired, JsonPropertyName("verifiedAtUtc"), JsonPropertyOrder(9)] DateTimeOffset VerifiedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelPreflightCommandRejected(
    [property: JsonRequired, JsonPropertyName("schemaVersion"), JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonRequired, JsonPropertyName("operation"), JsonPropertyOrder(1)] string Operation,
    [property: JsonRequired, JsonPropertyName("accepted"), JsonPropertyOrder(2)] bool Accepted,
    [property: JsonRequired, JsonPropertyName("model"), JsonPropertyOrder(3)] string Model,
    [property: JsonRequired, JsonPropertyName("contextTokens"), JsonPropertyOrder(4)] int ContextTokens,
    [property: JsonRequired, JsonPropertyName("catalogVersion"), JsonPropertyOrder(5)] string CatalogVersion,
    [property: JsonRequired, JsonPropertyName("code"), JsonPropertyOrder(6)] string Code);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelCommandError(
    [property: JsonRequired, JsonPropertyName("schemaVersion"), JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonRequired, JsonPropertyName("operation"), JsonPropertyOrder(1)] string Operation,
    [property: JsonRequired, JsonPropertyName("accepted"), JsonPropertyOrder(2)] bool Accepted,
    [property: JsonRequired, JsonPropertyName("code"), JsonPropertyOrder(3)] string Code);
