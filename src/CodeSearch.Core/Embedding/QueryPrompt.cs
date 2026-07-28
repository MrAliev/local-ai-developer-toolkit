namespace CodeSearch.Core.Embedding;

/// <summary>
/// Qwen3-Embedding is trained asymmetrically: documents are embedded as plain text, queries are
/// embedded with an instruction prefix describing the retrieval task. Skipping the prefix leaves
/// measurable retrieval quality on the table, and the model's own documentation specifies this
/// exact shape.
///
/// The prefix is applied ONLY to queries, so it changes nothing about an already-built index -
/// switching it on does not require a rebuild.
/// </summary>
public static class QueryPrompt
{
    private const string Task =
        "Given a code search question, retrieve the code chunk (class, method, or file section) that answers it";

    /// <summary>
    /// Wraps a query for models that were trained with instruction prefixes, and leaves it alone
    /// for models that were not - feeding "Instruct: ..." to a model that never saw that format
    /// just adds noise to the vector.
    /// </summary>
    public static string ForQuery(string model, string query) =>
        UsesInstructions(model) ? $"Instruct: {Task}\nQuery: {query}" : query;

    public static bool UsesInstructions(string model) =>
        model.Contains("qwen3-embedding", StringComparison.OrdinalIgnoreCase);
}
