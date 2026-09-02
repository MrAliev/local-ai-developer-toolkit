using System.Text;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// One root cause said once, with a count for the rest.
///
/// A repository whose NuGet configuration trips package-source mapping gets a workspace failure
/// per project — 53 of them on the repository that reported this, every one the same sentence
/// with a different project path in it. The degradation is graceful and the sync still finishes;
/// the cost is that anything actually new in the stream drowns (#291).
///
/// What counts as the same failure is the sentence with its quoted parts removed. The path is
/// what differs between them and it sits in quotes, so this survives a change in Roslyn's
/// wording — which keying on "with message:" would not. It is deliberately blunt: two failures
/// that differ only inside quotes are treated as one, and the first is printed whole, so the
/// path that was dropped is still on screen.
/// </summary>
public sealed class WorkspaceDiagnosticDigest
{
    // Roslyn raises workspace failures while it loads projects, and it loads them in parallel.
    private readonly Lock gate = new();
    private readonly Dictionary<(string Kind, string Shape), int> seen = [];
    private readonly List<(string Kind, string Shape)> order = [];

    /// <summary>
    /// The line to print, or null when this failure has already been said in another form.
    /// </summary>
    public string? Observe(string kind, string message)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(message);

        var key = (kind, Shape(message));
        lock (gate)
        {
            if (seen.TryGetValue(key, out var count))
            {
                seen[key] = count + 1;
                return null;
            }

            seen[key] = 1;
            order.Add(key);
            return message;
        }
    }

    /// <summary>
    /// One line per failure that repeated, in the order the failures were first seen. Nothing
    /// for a failure that happened once: a summary line after a single failure would be noise
    /// of exactly the kind this exists to remove.
    /// </summary>
    public IReadOnlyList<string> Summarise()
    {
        var lines = new List<string>();
        lock (gate)
        {
            foreach (var key in order)
            {
                var more = seen[key] - 1;
                if (more <= 0)
                {
                    continue;
                }

                lines.Add(
                    $"{key.Kind}: the same failure for {more} " +
                    (more == 1 ? "more project" : "more projects") +
                    " (suppressed; the first is above).");
            }
        }

        return lines;
    }

    /// <summary>
    /// The sentence with everything between single quotes taken out. An unbalanced quote leaves
    /// the rest of the line in place rather than swallowing it, so a message that merely
    /// contains an apostrophe still groups by what it says.
    /// </summary>
    private static string Shape(string message)
    {
        if (!message.Contains('\'', StringComparison.Ordinal))
        {
            return message;
        }

        var shape = new StringBuilder(message.Length);
        var quoted = false;
        foreach (var character in message)
        {
            if (character == '\'')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted)
            {
                shape.Append(character);
            }
        }

        // An odd number of quotes means the last one opened a run that never closed, and
        // everything after it was dropped. Grouping on a truncated sentence would merge
        // failures that say different things, so the original is used instead.
        return quoted ? message : shape.ToString();
    }
}
