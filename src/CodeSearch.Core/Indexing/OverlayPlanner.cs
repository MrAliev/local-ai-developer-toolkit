namespace CodeSearch.Core.Indexing;

public static class OverlayPlanner
{
    public static IReadOnlyList<OverlayIdentity> Plan(
        string generationId,
        string baseCommit,
        string baseTree,
        IReadOnlyList<CommitNode> firstParentHistory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseTree);
        if (firstParentHistory.Count == 0)
        {
            return [];
        }

        var ordered = firstParentHistory.Reverse().ToArray();
        var connected = string.Equals(
            ordered[0].FirstParentCommit,
            baseCommit,
            StringComparison.Ordinal);
        for (var index = 1; connected && index < ordered.Length; index++)
        {
            connected = string.Equals(
                ordered[index].FirstParentCommit,
                ordered[index - 1].Commit,
                StringComparison.Ordinal);
        }

        if (!connected)
        {
            var target = firstParentHistory[0];
            return
            [
                new OverlayIdentity(
                    generationId,
                    baseTree,
                    target.Tree,
                    target.Commit,
                    OverlayKind.Collapsed,
                    null)
            ];
        }

        return ordered.Select(node => new OverlayIdentity(
                generationId,
                node.FirstParentTree ?? baseTree,
                node.Tree,
                node.Commit,
                OverlayKind.Commit,
                null))
            .ToArray();
    }

    public static OverlayIdentity Dirty(
        string generationId,
        string baseTree,
        string commit,
        string contentHash) =>
        new(
            generationId,
            baseTree,
            baseTree,
            commit,
            OverlayKind.Dirty,
            contentHash);
}
