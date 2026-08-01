using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

public sealed record BrokerPolicy(
    int SchemaVersion,
    ModelResidencyPolicy ModelResidency)
{
    public const string FileName = "policy.json";

    public static BrokerPolicy Default { get; } =
        new(1, ModelResidencyPolicy.RequireFullVram);
}

/// <summary>
/// Installation-wide broker policy, stored in the runtime root so the broker, the CLI and the
/// installer all read the same document instead of each holding its own opinion.
///
/// A missing, empty or malformed file yields <see cref="BrokerPolicy.Default"/>. A machine
/// that was never configured and a machine whose policy file got corrupted must both land on
/// the strict setting — a parse error must never silently relax a safety check.
/// </summary>
public sealed class ModelResidencyPolicyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly string _path;

    public ModelResidencyPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _path = Path.Combine(runtimeRoot, BrokerPolicy.FileName);
    }

    /// <summary>
    /// The runtime root every LocalAi component uses when none was passed explicitly.
    /// </summary>
    public static string DefaultRuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi");

    public static BrokerPolicy ReadDefault() =>
        new ModelResidencyPolicyStore(DefaultRuntimeRoot).Read();

    public BrokerPolicy Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return BrokerPolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<BrokerPolicy>(
                File.ReadAllBytes(_path),
                SerializerOptions);
            if (policy is null ||
                policy.SchemaVersion != BrokerPolicy.Default.SchemaVersion ||
                !Enum.IsDefined(policy.ModelResidency))
            {
                return BrokerPolicy.Default;
            }

            return policy;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return BrokerPolicy.Default;
        }
    }

    public void Write(BrokerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(policy, SerializerOptions));
    }
}
