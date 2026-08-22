using System.Globalization;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Models;

namespace LocalAi.ReleaseSigner;

/// <summary>
/// Builds the model list a release is signed with.
///
/// The installer only installs models it can tie back to the signed manifest — the routing
/// catalogue and the public registry are what a human is shown, and neither is signed. So a
/// release whose manifest carries no models installs none, whatever the wizard offered on the
/// models page. That is exactly what shipped: <c>--models</c> was optional, the publish script
/// never passed it, and every release from 0.1.29 through 0.1.44 was signed with an empty
/// list. The model page kept promising six models, the run kept installing zero, and the log
/// line explaining it sat under a green "Installation complete".
///
/// Sizes are read from the registry at signing time rather than committed to the repository.
/// A model republished with different quantisation keeps its tag, so a size stored in the
/// source tree goes stale silently and the installer starts weighing the wrong number against
/// somebody's video memory. Reading it per release makes the number as old as the release
/// itself and no older.
///
/// A tag whose size cannot be read is a failure, not an omission. Skipping it would produce a
/// release that quietly cannot install that model — the same class of failure this whole file
/// exists to end.
/// </summary>
public static class ReleaseModelList
{
    /// <summary>
    /// The manifest verifier's own ceiling. Reaching it means the catalogue grew past what a
    /// manifest can carry, which has to stop a release rather than truncate one.
    /// </summary>
    private const int MaximumEntries = 128;

    public sealed record Entry(
        string Name,
        int ContextTokens,
        long DownloadSize,
        long EstimatedVramBytes);

    public static async Task<IReadOnlyList<Entry>> BuildAsync(
        IModelSizeSource sizeSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sizeSource);
        var models = ModelRoutingCatalogResource.SelectableModels();
        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                "The embedded routing catalogue offers no model, so there is nothing to sign.");
        }

        var entries = new List<Entry>();
        foreach (var model in models)
        {
            var size = await sizeSource
                .GetDownloadSizeBytesAsync(model.Tag, cancellationToken)
                .ConfigureAwait(false);
            if (size is not > 0)
            {
                throw new InvalidOperationException(
                    $"The download size of '{model.Tag}' could not be read from the model " +
                    "registry. Signing a release without it would ship an installer that " +
                    "cannot install that model. Check the tag and the network, then retry.");
            }

            foreach (var context in model.ContextTokens.Distinct().OrderBy(value => value))
            {
                if (!IsSupportedContext(context))
                {
                    throw new InvalidOperationException(
                        $"'{model.Tag}' declares a context of {context} tokens, which a " +
                        "release manifest cannot carry: it must be a power of two between " +
                        "2048 and 262144. Fix the routing catalogue.");
                }

                entries.Add(new Entry(
                    model.Tag,
                    context,
                    size.Value,
                    // The recommendation engine treats this as the base weight and adds the
                    // runtime and per-token reserves itself, so the download size is the
                    // right value here and an inflated guess would be double counting.
                    size.Value));
            }
        }

        if (entries.Count > MaximumEntries)
        {
            throw new InvalidOperationException(
                $"The catalogue produces {entries.Count} model options, and a release " +
                $"manifest accepts at most {MaximumEntries}.");
        }

        return entries;
    }

    /// <summary>
    /// Written by hand rather than serialized, for the same reason the manifest is: this
    /// document is read back by <c>sign</c>, and a stable, boring shape is easier to diff in
    /// a release log than whatever a serializer settles on.
    /// </summary>
    public static string Render(IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        builder.Append('[');
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            builder.AppendLine(index == 0 ? string.Empty : ",");
            builder.Append(
                CultureInfo.InvariantCulture,
                $"  {{\"Name\":{System.Text.Json.JsonSerializer.Serialize(entry.Name)}," +
                $"\"ContextTokens\":{entry.ContextTokens}," +
                $"\"DownloadSize\":{entry.DownloadSize}," +
                $"\"EstimatedVramBytes\":{entry.EstimatedVramBytes}}}");
        }

        builder.AppendLine();
        builder.AppendLine("]");
        return builder.ToString();
    }

    private static bool IsSupportedContext(int value) =>
        value is >= 2048 and <= 262144 && (value & (value - 1)) == 0;
}
