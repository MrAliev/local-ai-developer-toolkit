namespace LocalLm.Core;

/// <summary>
/// Estimates how many cloud tokens a delegated job avoided spending.
///
/// The point is to make the saving a *measurement of what was actually processed* rather than a
/// number invented after the fact. It is still an estimate - there is no live token counter here -
/// so every figure is reported as a range, never as an exact count.
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// Latin text and source code sit near 4 characters per token.
    /// </summary>
    private const double LatinCharsPerToken = 4.0;

    /// <summary>
    /// Cyrillic is far less efficient - most tokenizers split Russian into 2-3 character pieces,
    /// which matters a lot here: this codebase's comments, commit messages and docs are Russian,
    /// so assuming the Latin ratio would understate the saving by roughly half.
    /// </summary>
    private const double CyrillicCharsPerToken = 2.2;

    /// <summary>
    /// Image cost scales with pixel area, at roughly width x height / 750 tokens.
    /// </summary>
    private const double PixelsPerImageToken = 750.0;

    public static int ForText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cyrillic = 0;
        var letters = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch))
            {
                continue;
            }

            letters++;
            if (ch is >= 'Ѐ' and <= 'ӿ')
            {
                cyrillic++;
            }
        }

        var cyrillicShare = letters == 0 ? 0 : (double)cyrillic / letters;
        var charsPerToken = (LatinCharsPerToken * (1 - cyrillicShare)) + (CyrillicCharsPerToken * cyrillicShare);

        return (int)Math.Ceiling(text.Length / charsPerToken);
    }

    public static int ForImage(ImageInfo image) =>
        (int)Math.Ceiling(image.Width * (double)image.Height / PixelsPerImageToken);

    /// <summary>
    /// What the job would have cost in the cloud, minus what it actually cost (the short answer
    /// that came back). Never negative - a delegation that returns more than it consumed saved
    /// nothing, it did not cost extra.
    /// </summary>
    public static int Saved(int wouldHaveCost, string actualAnswer) =>
        Math.Max(0, wouldHaveCost - ForText(actualAnswer));

    public static TranslationTokenMetrics ForTranslation(
        string source,
        string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);
        var input = ForText(source);
        var output = ForText(translated);
        return new TranslationTokenMetrics(
            input,
            output,
            checked(input + output),
            output,
            Math.Max(0, input - output));
    }

    /// <summary>
    /// Formats a saving as a deliberately coarse range. Presenting "~18,437 tokens" would imply a
    /// precision that does not exist.
    /// </summary>
    public static string Describe(int saved) => saved switch
    {
        0 => "0",
        < 500 => "менее ~0.5K",
        < 100_000 => $"~{Round(saved * 0.85)}–{Round(saved * 1.15)}K",
        _ => $"~{Round(saved * 0.8)}–{Round(saved * 1.2)}K",
    };

    private static string Round(double tokens)
    {
        var thousands = tokens / 1000.0;
        return thousands >= 10 ? Math.Round(thousands).ToString("0") : Math.Round(thousands, 1).ToString("0.#");
    }
}

public sealed record TranslationTokenMetrics(
    int InputTokens,
    int OutputTokens,
    int LocalTokensProcessed,
    int EstimatedCloudGenerationTokensSaved,
    int EstimatedNetCloudContextTokensSaved);
