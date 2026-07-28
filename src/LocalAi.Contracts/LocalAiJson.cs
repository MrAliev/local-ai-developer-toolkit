using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LocalAi.Contracts;

public static class LocalAiJson
{
    public static JsonSerializerOptions Strict { get; } = CreateStrictOptions();

    private static JsonSerializerOptions CreateStrictOptions()
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
