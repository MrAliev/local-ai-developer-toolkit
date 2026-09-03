namespace CodeSearch.Core.Semantics;

/// <summary>
/// The seam between how a position is written down and how it is stored.
///
/// Everything inside counts from zero, because that is the wire convention this index is built
/// on — LSP, SCIP and Roslyn's <c>LinePosition</c> all do — and re-deriving every imported
/// position to move it would cost more than it could ever buy. Everything a person or a model
/// reads counts from one, because `search_code` prints that, an editor shows that, and
/// `path:line` is a notation shared with ripgrep, compiler diagnostics, stack traces and
/// permalinks rather than a private one.
///
/// The two used to meet nowhere at all: a printed line handed to a navigation tool landed one
/// line into the body, which was a refusal when nothing was there and a confident answer about
/// the wrong symbol when an identifier was. So the conversion lives here, once, at the edge —
/// called by the MCP tools and by the CLI, and directly testable — rather than inside the
/// gateway, where it would be shared with callers that already speak zero-based and would stop
/// being visible.
/// </summary>
public readonly record struct SourcePosition(int Line, int Utf16Column)
{
    /// <summary>
    /// The stored position for a line and column as they were printed, or false when either is
    /// below one.
    ///
    /// A zero is refused rather than clamped or passed through: it can only be a position
    /// somebody counted from zero, and answering about the line above it would be the same
    /// silent wrongness this conversion exists to end.
    /// </summary>
    public static bool TryFromOneBased(int line, int utf16Column, out SourcePosition position)
    {
        if (line < 1 || utf16Column < 1)
        {
            position = default;
            return false;
        }

        position = new SourcePosition(line - 1, utf16Column - 1);
        return true;
    }

    /// <summary>A stored line as it is printed: what an editor would put in its gutter.</summary>
    public static int ToOneBased(int storedLineOrColumn) => storedLineOrColumn + 1;
}
