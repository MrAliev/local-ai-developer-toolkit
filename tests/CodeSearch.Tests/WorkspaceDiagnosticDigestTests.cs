using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// One root cause, 53 lines. A repository whose NuGet configuration trips source mapping gets a
/// workspace failure per project, every one of them the same sentence with a different project
/// path in it — and the hostfxr line, or a genuinely different failure, drowns in them (#291).
///
/// What makes two of them the same is deliberately not their exact text: the path is what
/// differs, and it sits in quotes. Grouping on the sentence with its quoted parts taken out
/// survives a change in Roslyn's wording, which keying on "with message:" would not.
/// </summary>
public sealed class WorkspaceDiagnosticDigestTests
{
    private const string SourceMapping =
        "Msbuild failed when processing the file '{0}' with message: " +
        "В вашей конфигурации определены 2 источника пакетов";

    [Fact]
    public void The_first_of_a_repeated_failure_is_shown_in_full()
    {
        var digest = new WorkspaceDiagnosticDigest();

        var line = digest.Observe("Failure", string.Format(SourceMapping, "A.csproj"));

        Assert.Equal(string.Format(SourceMapping, "A.csproj"), line);
    }

    [Fact]
    public void The_rest_are_held_back_and_counted()
    {
        var digest = new WorkspaceDiagnosticDigest();
        digest.Observe("Failure", string.Format(SourceMapping, "A.csproj"));

        Assert.Null(digest.Observe("Failure", string.Format(SourceMapping, "B.csproj")));
        Assert.Null(digest.Observe("Failure", string.Format(SourceMapping, "C.csproj")));

        var summary = Assert.Single(digest.Summarise());
        Assert.Equal(
            "Failure: the same failure for 2 more projects (suppressed; the first is above).",
            summary);
    }

    /// <summary>
    /// A failure that says something else has to come through. Volume is the complaint; hiding
    /// the one line that was worth reading would be the same complaint with fewer lines.
    /// </summary>
    [Fact]
    public void A_different_failure_is_not_swallowed_by_a_repeated_one()
    {
        var digest = new WorkspaceDiagnosticDigest();
        digest.Observe("Failure", string.Format(SourceMapping, "A.csproj"));
        digest.Observe("Failure", string.Format(SourceMapping, "B.csproj"));

        var other = digest.Observe("Failure", "Dll was not found.");

        Assert.Equal("Dll was not found.", other);
    }

    [Fact]
    public void The_same_words_at_a_different_severity_are_different_failures()
    {
        var digest = new WorkspaceDiagnosticDigest();
        digest.Observe("Failure", "Something went wrong.");

        Assert.Equal("Something went wrong.", digest.Observe("Warning", "Something went wrong."));
    }

    /// <summary>
    /// Nothing repeated, nothing to say. A summary line after a single failure would be noise
    /// of exactly the kind this exists to remove.
    /// </summary>
    [Fact]
    public void A_failure_that_happened_once_gets_no_summary()
    {
        var digest = new WorkspaceDiagnosticDigest();
        digest.Observe("Failure", string.Format(SourceMapping, "A.csproj"));

        Assert.Empty(digest.Summarise());
    }

    [Fact]
    public void One_more_reads_as_one_rather_than_as_1_projects()
    {
        var digest = new WorkspaceDiagnosticDigest();
        digest.Observe("Failure", string.Format(SourceMapping, "A.csproj"));
        digest.Observe("Failure", string.Format(SourceMapping, "B.csproj"));

        Assert.Equal(
            "Failure: the same failure for 1 more project (suppressed; the first is above).",
            Assert.Single(digest.Summarise()));
    }
}
