using System.Text;

namespace LocalAi.Broker;

/// <summary>
/// Turns a model backend's pull stream into positions a reader can act on.
///
/// Three jobs, all of which have to happen here because this is the only place that sees the
/// whole stream. It sums the layers, because the backend counts each one from zero and a line
/// straight from it would read as a failure and a retry six times in one download. It names the
/// phase in words this product owns, because a backend's vocabulary is not ours to freeze. And it
/// refuses to publish faster than a reader could use, because every publication is a file written
/// beside the job.
///
/// Two figures and never a percent: the denominator is the sum of the layer sizes known so far,
/// so it grows as digests appear, and a percent against a growing denominator goes backwards.
/// Both figures here only ever increase.
/// </summary>
public sealed class ModelPullTracker(Func<DateTimeOffset> clock)
{
    /// <summary>
    /// The quietest this will be. A reader cannot act on more than one position every few
    /// seconds, and neither can the file it is written to.
    /// </summary>
    private static readonly TimeSpan Quietest = TimeSpan.FromSeconds(2);

    /// <summary>
    /// External text on its way onto one console line. Cut here rather than at the console so the
    /// durable file cannot hold what the line could not show.
    /// </summary>
    private const int MaximumDetailCharacters = 60;

    private readonly Dictionary<string, (long Completed, long Total)> layers =
        new(StringComparer.Ordinal);

    private string? phase;
    private string? detail;
    private DateTimeOffset? publishedAt;

    public JobProgress? Accept(ModelPullProgress line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var status = line.Status ?? string.Empty;

        // The job's own completion says this better, and a phase line here would be a claim about
        // a run that is already over.
        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var next = PhaseOf(status, line);
        var nextDetail = next == "other" ? OneLine(status) : null;
        if (next == "downloading" && line.Digest is { } digest)
        {
            // Assigned, not added: a layer reporting again is replacing what it said before, and
            // adding would climb past the size of the download.
            layers[digest] = (line.Completed, line.Total);
        }

        var now = clock();
        var moved = !string.Equals(phase, next, StringComparison.Ordinal) ||
                    !string.Equals(detail, nextDetail, StringComparison.Ordinal);
        if (!moved && publishedAt is { } last && now - last < Quietest)
        {
            return null;
        }

        phase = next;
        detail = nextDetail;
        publishedAt = now;
        return new JobProgress(
            next,
            nextDetail,
            layers.Values.Sum(layer => layer.Completed),
            layers.Values.Sum(layer => layer.Total));
    }

    /// <summary>
    /// A layer line is one that carries a digest and a size; everything else is named by what the
    /// backend called it, and anything unrecognised keeps the backend's own word rather than
    /// being dressed as the nearest phase this product knows.
    /// </summary>
    private static string PhaseOf(string status, ModelPullProgress line)
    {
        if (line.Digest is not null && line.Total > 0)
        {
            return "downloading";
        }

        if (status.StartsWith("pulling manifest", StringComparison.OrdinalIgnoreCase))
        {
            return "preparing";
        }

        if (status.StartsWith("verifying", StringComparison.OrdinalIgnoreCase))
        {
            return "verifying";
        }

        return status.StartsWith("writing manifest", StringComparison.OrdinalIgnoreCase) ||
               status.StartsWith("removing", StringComparison.OrdinalIgnoreCase)
            ? "storing"
            : "other";
    }

    /// <summary>
    /// Whitespace collapsed so the text cannot forge a second line, then cut so it cannot fill
    /// the screen. Both guards exist because this is somebody else's text.
    /// </summary>
    private static string OneLine(string status)
    {
        var text = new StringBuilder(status.Length);
        var space = false;
        foreach (var character in status)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = text.Length > 0;
                continue;
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            text.Append(character);
            if (text.Length == MaximumDetailCharacters)
            {
                break;
            }
        }

        return text.ToString();
    }
}
