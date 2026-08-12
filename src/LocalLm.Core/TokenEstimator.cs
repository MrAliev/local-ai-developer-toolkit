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
        _ => DescribeRange(saved),
    };

    /// <summary>
    /// The band on its own, without a sentence around it, for callers reporting in another
    /// language or over a total rather than a single job.
    ///
    /// The width is the same judgement either way: a fifteen per cent band up to a hundred
    /// thousand tokens, twenty above it, because the further the estimate is extrapolated from
    /// characters the less the middle of it is worth. A month of broker telemetry sums past what
    /// thousands can express, so the unit follows the number instead of being fixed at K.
    /// </summary>
    public static string DescribeRange(long saved)
    {
        if (saved <= 0)
        {
            return "0";
        }

        var band = saved < 100_000 ? 0.15 : 0.2;
        var low = saved * (1 - band);
        var high = saved * (1 + band);
        return high >= 1_000_000
            ? $"~{Round(low / 1000.0)}–{Round(high / 1000.0)}M"
            : $"~{Round(low)}–{Round(high)}K";
    }

    /// <summary>
    /// The whole sentence about the saving, rather than a number to drop into one.
    ///
    /// A zero here is a real and correct answer — a 336x52 image is worth about two dozen tokens
    /// to look at, and any useful answer about it is longer than that, so delegating it saved
    /// nothing. But "Сэкономлено примерно 0 облачных токенов" reads as a broken counter rather
    /// than as a job too small to be worth delegating, and the difference matters: one is a bug
    /// report, the other is advice.
    /// </summary>
    public static string DescribeSaving(int saved) => saved switch
    {
        0 => "Облачных токенов это не сэкономило: ответ не короче исходных данных.",
        < 500 => "Сэкономлено пренебрежимо мало облачных токенов — менее ~0.5K.",
        _ => $"Сэкономлено примерно {Describe(saved)} облачных токенов.",
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
