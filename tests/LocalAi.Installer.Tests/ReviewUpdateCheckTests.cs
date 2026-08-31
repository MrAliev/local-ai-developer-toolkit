using LocalAi.Contracts;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The consent the review page asks for.
///
/// An opt-in is only an opt-in if the default is no and the question is answerable without
/// reading anything else — so the box starts empty and the sentence beside it is the same one
/// the CLI prints when it takes the same consent.
/// </summary>
public sealed class ReviewUpdateCheckTests
{
    [Fact]
    public void The_box_starts_empty()
    {
        var review = new ReviewApplyPageViewModel();

        Assert.False(review.EnableUpdateCheck);
    }

    [Fact]
    public void Answering_it_does_not_gate_the_installation()
    {
        var review = new ReviewApplyPageViewModel { IsConfirmed = true };

        Assert.True(review.CanApply);

        review.EnableUpdateCheck = true;

        Assert.True(review.CanApply);
    }

    /// <summary>
    /// One sentence, held in the contract, so the wizard's checkbox and
    /// `localai policy set --update-check on` cannot drift into describing the same request
    /// differently.
    /// </summary>
    [Fact]
    public void The_sentence_beside_it_is_the_one_the_cli_prints()
    {
        var review = new ReviewApplyPageViewModel();

        Assert.Equal(UpdateCheckPolicy.Disclosure, review.UpdateCheckDisclosure);
    }

    [Theory]
    [InlineData("Nothing about this machine is sent")]
    [InlineData("verifies the signature")]
    [InlineData("without you asking")]
    public void It_says_what_is_sent_and_what_is_not(string claim) =>
        Assert.Contains(
            claim,
            new ReviewApplyPageViewModel().UpdateCheckDisclosure,
            StringComparison.Ordinal);
}
