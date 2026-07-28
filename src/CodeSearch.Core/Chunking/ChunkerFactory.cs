namespace CodeSearch.Core.Chunking;

/// <summary>
/// Decides which chunker (if any) handles a given file. Returning null means "don't index this".
/// </summary>
public static class ChunkerFactory
{
    private static readonly RoslynChunker CSharp = new();
    private static readonly GenericTextChunker Generic = new();

    /// <summary>
    /// Text formats worth embedding. Deliberately excludes lock files, minified bundles and
    /// anything else that is machine-written: those add tens of thousands of chunks and never
    /// answer a question anyone actually asks.
    /// </summary>
    private static readonly HashSet<string> GenericExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".vue", ".svelte",
        ".py", ".go", ".java", ".kt", ".rs", ".rb", ".php",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".ino",
        ".sql", ".graphql", ".proto",
        ".xaml", ".razor", ".cshtml", ".html", ".css", ".scss", ".less",
        ".md", ".mdx", ".txt", ".adoc",
        ".yml", ".yaml", ".toml", ".ini", ".conf",
        ".ps1", ".psm1", ".sh", ".bash",
        ".tf", ".bicep",
        ".csproj", ".props", ".targets", ".fsproj", ".vbproj",
        ".json",
    };

    private static readonly string[] ExcludedFileMarkers =
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", ".min.js", ".min.css",
        ".designer.cs", ".g.cs", ".g.i.cs", "assemblyinfo.cs", ".generated.cs",
    };

    public static bool IsIndexable(string path) => Resolve(path) is not null;

    public static IChunker? Resolve(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var marker in ExcludedFileMarkers)
        {
            if (fileName.EndsWith(marker, StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var ext = Path.GetExtension(path);
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".csx", StringComparison.OrdinalIgnoreCase))
        {
            return CSharp;
        }

        return GenericExtensions.Contains(ext) ? Generic : null;
    }
}
