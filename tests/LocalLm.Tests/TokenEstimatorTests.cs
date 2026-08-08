using LocalLm.Core;

namespace LocalLm.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void CyrillicCostsRoughlyTwiceAsManyTokensAsLatinForTheSameLength()
    {
        var latin = new string('a', 1000);
        var cyrillic = new string('ф', 1000);

        var latinTokens = TokenEstimator.ForText(latin);
        var cyrillicTokens = TokenEstimator.ForText(cyrillic);

        // This is the whole reason the estimator inspects the text: assuming the Latin ratio on
        // this codebase's Russian comments and commit messages would understate savings by ~half.
        Assert.True(cyrillicTokens > latinTokens * 1.5,
            $"expected Cyrillic to cost meaningfully more, got {cyrillicTokens} vs {latinTokens}");
    }

    [Fact]
    public void MixedTextLandsBetweenThePureCases()
    {
        var mixed = string.Concat(Enumerable.Repeat("public void Закрыть() // закрывает заказ\n", 25));
        var tokens = TokenEstimator.ForText(mixed);

        Assert.InRange(tokens, mixed.Length / 4, mixed.Length / 2);
    }

    [Fact]
    public void EmptyTextCostsNothing()
    {
        Assert.Equal(0, TokenEstimator.ForText(string.Empty));
        Assert.Equal(0, TokenEstimator.ForText(null!));
    }

    [Fact]
    public void ImageCostScalesWithPixelsNotFileSize()
    {
        var small = TokenEstimator.ForImage(new ImageInfo(800, 600, "png"));
        var large = TokenEstimator.ForImage(new ImageInfo(1600, 1200, "png"));

        Assert.Equal(small * 4, large);
        Assert.InRange(TokenEstimator.ForImage(new ImageInfo(1920, 1080, "png")), 2000, 3500);
    }

    [Fact]
    public void SavingSubtractsTheAnswerAndNeverGoesNegative()
    {
        Assert.Equal(0, TokenEstimator.Saved(10, new string('a', 4000)));

        var saved = TokenEstimator.Saved(50_000, "короткий ответ");
        Assert.InRange(saved, 49_000, 50_000);
    }

    [Fact]
    public void Translation_metrics_separate_local_work_generation_and_context_delta()
    {
        var metrics = TokenEstimator.ForTranslation(
            new string('a', 4_000),
            new string('ф', 2_200));

        Assert.Equal(1_000, metrics.InputTokens);
        Assert.Equal(1_000, metrics.OutputTokens);
        Assert.Equal(2_000, metrics.LocalTokensProcessed);
        Assert.Equal(1_000, metrics.EstimatedCloudGenerationTokensSaved);
        Assert.Equal(0, metrics.EstimatedNetCloudContextTokensSaved);
    }

    [Theory]
    [InlineData(100, "менее")]
    [InlineData(20_000, "–")]
    [InlineData(250_000, "–")]
    public void DescribeAlwaysReportsARangeNeverAnExactCount(int saved, string expected)
    {
        var text = TokenEstimator.Describe(saved);

        Assert.Contains(expected, text);
        Assert.DoesNotContain(saved.ToString(), text);
    }

    [Fact]
    public void A_job_too_small_to_save_anything_says_so_instead_of_reporting_zero()
    {
        // A 336x52 screenshot is about two dozen tokens to look at directly, and any useful
        // answer about it is longer than that. Zero is the correct arithmetic; "Сэкономлено
        // примерно 0 облачных токенов" reads as a broken counter rather than as a job not worth
        // delegating.
        var image = TokenEstimator.ForImage(new ImageInfo(336, 52, "png"));
        var saved = TokenEstimator.Saved(image, new string('a', 4_000));

        var sentence = TokenEstimator.DescribeSaving(saved);

        Assert.Equal(0, saved);
        Assert.DoesNotContain("примерно 0", sentence, StringComparison.Ordinal);
        Assert.Contains("не сэкономило", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_small_but_real_saving_is_called_negligible_rather_than_counted()
    {
        var sentence = TokenEstimator.DescribeSaving(100);

        Assert.Contains("пренебрежимо мало", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("100", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saving_worth_reporting_still_reports_a_range()
    {
        var sentence = TokenEstimator.DescribeSaving(20_000);

        Assert.Contains("–", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("20000", sentence, StringComparison.Ordinal);
    }
}
