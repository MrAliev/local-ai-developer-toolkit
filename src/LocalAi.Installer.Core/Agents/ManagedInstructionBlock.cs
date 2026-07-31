namespace LocalAi.Installer.Core.Agents;

public sealed record ManagedInstructionBlockResult(bool Changed, string Content);

public static class ManagedInstructionBlock
{
    public const string BeginMarker = "<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->";
    public const string EndMarker = "<!-- END LOCALAI MANAGED INSTRUCTIONS -->";

    public static readonly string Block =
        BeginMarker + Environment.NewLine +
        "Use only the shared LocalAi FIFO broker for local-model work." + Environment.NewLine +
        "Never access Ollama directly." + Environment.NewLine +
        "Require full-VRAM, zero-offload validation." + Environment.NewLine +
        EndMarker;

    public static ManagedInstructionBlockResult Upsert(string? content)
    {
        content ??= string.Empty;
        var beginIndexes = AllIndexesOf(content, BeginMarker);
        var endIndexes = AllIndexesOf(content, EndMarker);
        if (beginIndexes.Count > 1 || endIndexes.Count > 1 ||
            beginIndexes.Count != endIndexes.Count ||
            (beginIndexes.Count == 1 && beginIndexes[0] > endIndexes[0]))
        {
            throw new InvalidOperationException("Malformed managed instruction markers.");
        }

        string updated;
        if (beginIndexes.Count == 0)
        {
            var prefix = content.Length == 0 || content.EndsWith('\n')
                ? content
                : content + Environment.NewLine;
            updated = prefix + Block + Environment.NewLine;
        }
        else
        {
            var begin = beginIndexes[0];
            var end = endIndexes[0] + EndMarker.Length;
            updated = content[..begin] + Block + content[end..];
        }

        return new(!string.Equals(content, updated, StringComparison.Ordinal), updated);
    }

    private static List<int> AllIndexesOf(string content, string marker)
    {
        var indexes = new List<int>();
        var start = 0;
        while (true)
        {
            var index = content.IndexOf(marker, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return indexes;
            }

            indexes.Add(index);
            start = index + marker.Length;
        }
    }
}
