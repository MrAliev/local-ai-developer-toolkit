namespace LocalAi.Contracts;

/// <summary>
/// A policy, and where it came from.
///
/// Every policy store answers a file it cannot use — malformed, a schema it does not know, a
/// value out of range — with its safe defaults rather than an error. That is right at runtime:
/// nothing should fail to start because somebody mistyped a setting. It is also invisible, and
/// the file stays on disk looking configured while doing nothing at all.
///
/// Only the store knows which of the two it did, so this is how it says so. <c>FileFound</c> and
/// <c>FileUsed</c> are separate answers because "no file" and "a file that does nothing" are
/// different things to whoever wrote one.
/// </summary>
/// <param name="Path">
/// The file this answer came from, or would have. A policy can live in two places — the settings
/// directory, and the loose file an installation from before the split still has — so a surface
/// that resolves the path a second time can name one file while the store read the other.
/// </param>
public sealed record PolicyRead<T>(T Policy, string Path, bool FileFound, bool FileUsed);
