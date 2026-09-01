using System.Diagnostics.CodeAnalysis;

namespace LocalAi.Contracts;

/// <summary>
/// A filesystem path that has already been made canonical, so two of them can be compared.
///
/// Paths reach this codebase from sources that disagree about how to spell the same place. Git
/// prints forward slashes; .NET prints backslashes; a manifest keeps whatever was written into
/// it a month ago; Windows compares case-insensitively but a hash does not. Every one of those
/// differences has cost something here: a live worktree hashed one way and looked up another
/// read as "this worktree is gone", and its index was deleted — twice, by code written to
/// protect it.
///
/// A string cannot carry the fact that it has been normalised, so every comparison has to
/// remember to do it, and one that forgets still compiles and still looks right. This type
/// normalises once, where the path arrives, and comparison and hashing become its business
/// rather than each caller's.
///
/// Where it stops is worth knowing, because it looks like it goes further than it does. On
/// Windows a short (8.3) name is expanded for the segments that exist on disk — C:\PROGRA~1
/// canonicalises to C:\Program Files — but a segment that does not exist keeps whatever it was
/// given. A junction or symlink is never followed: the link and its target are two paths here,
/// and comparing them returns false. Both were measured rather than assumed. Where identity
/// across links has to hold, resolve the physical path first and build the FsPath from that.
/// </summary>
public readonly struct FsPath : IEquatable<FsPath>
{
    private readonly string? _value;

    private FsPath(string value) => _value = value;

    /// <summary>The canonical spelling: absolute, native separators, no trailing separator.</summary>
    public string Value => _value ?? throw new InvalidOperationException(
        "This FsPath was never given a path. Build one with FsPath.From where the path arrives, " +
        "so a default value cannot travel any further than that.");

    /// <summary>False for <c>default(FsPath)</c>, which carries no path at all.</summary>
    public bool HasValue => _value is not null;

    /// <summary>
    /// Canonicalises whatever spelling arrived. <see cref="Path.GetFullPath(string)"/> resolves
    /// relative segments and turns forward slashes native, which is what makes a path printed by
    /// git and one printed by .NET the same value here rather than two.
    /// </summary>
    public static FsPath From(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // TrimEnd, not Trim. A trailing newline is what a git subprocess leaves behind and no
        // directory is named for it. A LEADING space is a legal directory name on Windows, so
        // trimming the front would silently resolve " foo" to a different directory than the
        // one asked for — inventing a difference where this type exists to remove them.
        return new FsPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.TrimEnd())));
    }

    /// <summary>For values that are legitimately absent, such as an unset argument.</summary>
    public static FsPath? TryFrom(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : From(path);

    /// <summary>
    /// Appends segments beneath this path. Unlike <see cref="Path.Combine(string[])"/>, a
    /// rooted segment does not silently discard everything to its left — `Combine(@"C:\Windows")`
    /// returning C:\Windows out of a repository root is how a path built from configuration
    /// ends up somewhere nobody named. Escaping upwards with ".." is refused for the same
    /// reason: what comes back is always under what it was called on.
    /// </summary>
    public FsPath Combine(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var combined = From(Path.Combine([Value, .. segments]));
        if (combined != this && !combined.IdentityKey.StartsWith(
                IdentityKey.EndsWith(Path.DirectorySeparatorChar)
                    ? IdentityKey
                    : IdentityKey + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Combining '{string.Join("', '", segments)}' onto '{Value}' leaves it, " +
                $"landing at '{combined.Value}'. Build that path with FsPath.From instead, " +
                "so the escape is written down rather than arrived at.",
                nameof(segments));
        }

        return combined;
    }

    public string Name => Path.GetFileName(Value);

    public FsPath? Parent =>
        Path.GetDirectoryName(Value) is { Length: > 0 } parent ? From(parent) : null;

    public bool DirectoryExists => Directory.Exists(Value);

    public bool FileExists => File.Exists(Value);

    /// <summary>
    /// The spelling that anything deriving a stable key from a path must hash — case-folded
    /// where the filesystem is, left alone where it is not.
    ///
    /// This is load-bearing across releases: every index directory on every machine is named by
    /// a hash of this string. Changing what it produces renames every one of them, which reads
    /// on disk as every repository having lost its index.
    /// </summary>
    public string IdentityKey =>
        OperatingSystem.IsWindows() ? Value.ToUpperInvariant() : Value;

    /// <summary>
    /// Defined on <see cref="IdentityKey"/> rather than beside it, so the question "are these
    /// the same path" and the question "do these share an index directory" cannot be answered
    /// differently. They can: OrdinalIgnoreCase and ToUpperInvariant disagree about a handful
    /// of characters, and a pair this type called different while the key folded them together
    /// would be the very defect it was written to prevent, wearing the opposite face.
    /// </summary>
    public bool Equals(FsPath other) =>
        _value is null || other._value is null
            ? _value is null && other._value is null
            : string.Equals(IdentityKey, other.IdentityKey, StringComparison.Ordinal);

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is FsPath other && Equals(other);

    public override int GetHashCode() =>
        _value is null ? 0 : StringComparer.Ordinal.GetHashCode(IdentityKey);

    public override string ToString() => _value ?? string.Empty;

    public static bool operator ==(FsPath left, FsPath right) => left.Equals(right);

    public static bool operator !=(FsPath left, FsPath right) => !left.Equals(right);
}
