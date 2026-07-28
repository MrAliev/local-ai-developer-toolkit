namespace LocalAi.Repository;

public sealed record GitWorktree(
    string Path,
    string Head,
    string? Branch,
    bool IsDetached,
    bool IsPrunable);

public static class WorktreeInventory
{
    public static IReadOnlyList<GitWorktree> ParsePorcelain(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = new List<GitWorktree>();
        string? path = null;
        string? head = null;
        string? branch = null;
        var detached = false;
        var prunable = false;

        void Flush()
        {
            if (path is not null && head is not null)
            {
                result.Add(new GitWorktree(path, head, branch, detached, prunable));
            }

            path = null;
            head = null;
            branch = null;
            detached = false;
            prunable = false;
        }

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                Flush();
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line[9..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line[5..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = line[7..];
            }
            else if (line == "detached")
            {
                detached = true;
            }
            else if (line.StartsWith("prunable", StringComparison.Ordinal))
            {
                prunable = true;
            }
        }

        Flush();
        return result;
    }
}
