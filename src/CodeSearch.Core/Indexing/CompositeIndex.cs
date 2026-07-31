namespace CodeSearch.Core.Indexing;

/// <summary>
/// What the search engine needs from an index, so it can run over a single index or over a
/// base plus a branch overlay without knowing the difference.
/// </summary>
public interface ISearchableIndex
{
    int Dim { get; }
    string Model { get; }
    string RepositoryId { get; }
    string GenerationId { get; }
    string GitTree { get; }
    string? DirtyHash { get; }
    int ChunkCount { get; }
    ChunkMeta ChunkAt(int index);
    string PathOf(int index);
    ReadOnlySpan<byte> FileHashAt(int index);
    ReadOnlySpan<float> VectorAt(int index);
}

/// <summary>
/// A base index with a branch overlay laid over it.
///
/// The base is built once from the mainline checkout and shared by every worktree; an overlay
/// holds only what its branch changed. A branch that touches 389 files out of 8000 pays for 389,
/// not for a second full index - and one 684MB base serves every branch instead of one per branch.
///
/// Shadowing is per file path: if a path appears in the overlay (changed or added) or in its
/// deleted list, the base's chunks for that path are hidden entirely. Otherwise the file is
/// byte-identical to the base by construction, so the base's vectors are exactly right.
/// </summary>
public sealed class CompositeIndex : ISearchableIndex
{
    private readonly CodeIndex _base;
    private readonly CodeIndex _overlay;

    /// <summary>Base chunk indices that survive shadowing, in order.</summary>
    private readonly int[] _visibleBaseChunks;

    public CompositeIndex(CodeIndex baseIndex, CodeIndex overlay)
    {
        if (baseIndex.Dim != overlay.Dim ||
            !string.Equals(baseIndex.Model, overlay.Model, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Overlay was built with '{overlay.Model}' ({overlay.Dim} dims) but the base uses " +
                $"'{baseIndex.Model}' ({baseIndex.Dim} dims). Vectors from different models are not " +
                "comparable - rebuild the overlay.");
        }

        if (string.IsNullOrWhiteSpace(overlay.BaseCommit) ||
            !string.Equals(
                overlay.BaseCommit,
                baseIndex.GitCommit,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Overlay base '{overlay.BaseCommit}' does not match generation base " +
                $"'{baseIndex.GitCommit}'. No mixed base/overlay search is allowed.");
        }

        if (!string.IsNullOrWhiteSpace(baseIndex.RepositoryId) &&
            (!string.Equals(
                 overlay.RepositoryId,
                 baseIndex.RepositoryId,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 overlay.GenerationId,
                 baseIndex.GenerationId,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Overlay repository or generation does not match the immutable base generation.");
        }

        _base = baseIndex;
        _overlay = overlay;

        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in overlay.Files)
        {
            shadowed.Add(file.RelPath);
        }

        foreach (var deleted in overlay.DeletedPaths)
        {
            shadowed.Add(deleted);
        }

        var visible = new List<int>(baseIndex.Chunks.Count);
        for (var i = 0; i < baseIndex.Chunks.Count; i++)
        {
            if (!shadowed.Contains(baseIndex.Files[baseIndex.Chunks[i].FileIndex].RelPath))
            {
                visible.Add(i);
            }
        }

        _visibleBaseChunks = visible.ToArray();
    }

    public int Dim => _base.Dim;

    public string Model => _base.Model;

    public string RepositoryId => _base.RepositoryId;

    public string GenerationId => _base.GenerationId;

    public string GitTree => _overlay.GitTree;

    public string? DirtyHash => _overlay.DirtyHash;

    public int ChunkCount => _overlay.Chunks.Count + _visibleBaseChunks.Length;

    public int OverlayChunkCount => _overlay.Chunks.Count;

    public int ShadowedBaseChunks => _base.Chunks.Count - _visibleBaseChunks.Length;

    public int OverlayFileCount => _overlay.Files.Count;

    public int DeletedFileCount => _overlay.DeletedPaths.Count;

    public ChunkMeta ChunkAt(int index) => index < _overlay.Chunks.Count
        ? _overlay.Chunks[index]
        : _base.Chunks[_visibleBaseChunks[index - _overlay.Chunks.Count]];

    public string PathOf(int index) => index < _overlay.Chunks.Count
        ? _overlay.Files[_overlay.Chunks[index].FileIndex].RelPath
        : _base.Files[_base.Chunks[_visibleBaseChunks[index - _overlay.Chunks.Count]].FileIndex].RelPath;

    public ReadOnlySpan<byte> FileHashAt(int index) => index < _overlay.Chunks.Count
        ? _overlay.Files[_overlay.Chunks[index].FileIndex].Hash
        : _base.Files[
            _base.Chunks[_visibleBaseChunks[index - _overlay.Chunks.Count]].FileIndex].Hash;

    public ReadOnlySpan<float> VectorAt(int index) => index < _overlay.Chunks.Count
        ? _overlay.VectorAt(index)
        : _base.VectorAt(_visibleBaseChunks[index - _overlay.Chunks.Count]);
}
