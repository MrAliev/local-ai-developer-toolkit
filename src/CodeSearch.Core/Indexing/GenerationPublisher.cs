namespace CodeSearch.Core.Indexing;

public sealed class GenerationPublisher(GenerationStore store)
{
    public GenerationManifest Publish(
        string sourceIndexPath,
        GenerationIdentity identity,
        IReadOnlyList<OverlayReadiness> activeOverlays)
    {
        var generation = store.PublishIndex(sourceIndexPath, identity);
        var allReady = activeOverlays.All(
            overlay => overlay.Ready &&
                       string.Equals(
                           overlay.GenerationId,
                           identity.Id,
                           StringComparison.Ordinal));
        if (!allReady)
        {
            throw new InvalidOperationException(
                "The mainline generation is valid but active worktree overlays are not ready.");
        }

        store.SetCurrent(generation);
        return generation;
    }
}
