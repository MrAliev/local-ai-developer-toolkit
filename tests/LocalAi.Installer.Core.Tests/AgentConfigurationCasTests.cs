using System.Text;
using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The compare-and-swap contract of agent configuration apply and rollback (#203): a
/// concurrent writer's bytes must survive every race the installer can lose, and the
/// installer must refuse rather than clobber. The beforeSwap hook pins the exact window
/// the old check-then-replace shape left open — between the hash check and the swap.
/// </summary>
public sealed class AgentConfigurationCasTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-config-cas-" + Guid.NewGuid().ToString("N"));

    public AgentConfigurationCasTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task A_write_landing_between_check_and_swap_survives_and_the_apply_refuses()
    {
        var path = Path.Combine(_root, "config.toml");
        await File.WriteAllBytesAsync(
            path,
            Bytes("original"),
            TestContext.Current.CancellationToken);
        var plan = Plan(AgentConfigurationFileOperations.FilePlan(
            path,
            Bytes("original"),
            "installer",
            DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentConfigurationFileOperations.ApplyAsync(
                plan,
                File.ReadAllBytes,
                TestContext.Current.CancellationToken,
                beforeSwap: _ => File.WriteAllBytes(path, Bytes("concurrent"))));

        Assert.Contains("changed concurrently", error.Message, StringComparison.Ordinal);
        Assert.Equal("concurrent", Text(path));
    }

    [Fact]
    public async Task A_file_appearing_where_none_was_planned_stays_the_other_writers()
    {
        var path = Path.Combine(_root, "instructions.md");
        var plan = Plan(AgentConfigurationFileOperations.FilePlan(
            path,
            [],
            "installer",
            DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentConfigurationFileOperations.ApplyAsync(
                plan,
                File.ReadAllBytes,
                TestContext.Current.CancellationToken,
                beforeSwap: _ => File.WriteAllBytes(path, Bytes("concurrent"))));

        Assert.Contains("changed concurrently", error.Message, StringComparison.Ordinal);
        Assert.Equal("concurrent", Text(path));
    }

    [Fact]
    public async Task Rollback_keeps_a_file_another_writer_changed_after_apply()
    {
        var first = Path.Combine(_root, "first.md");
        var second = Path.Combine(_root, "second.md");
        await File.WriteAllBytesAsync(
            second,
            Bytes("what is really on disk"),
            TestContext.Current.CancellationToken);
        var plan = Plan(
            AgentConfigurationFileOperations.FilePlan(
                first,
                [],
                "one",
                DateTimeOffset.UtcNow),
            // The plan believes the second file holds something it no longer does, so its
            // apply refuses — after the first file has already been applied.
            AgentConfigurationFileOperations.FilePlan(
                second,
                Bytes("a stale preview"),
                "two",
                DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentConfigurationFileOperations.ApplyAsync(
                plan,
                path =>
                {
                    if (!string.Equals(path, first, StringComparison.OrdinalIgnoreCase))
                    {
                        return File.ReadAllBytes(path);
                    }

                    // An external writer edits the first file right after its apply; the
                    // read-back still reports what the apply wrote, as a reader with a
                    // stale handle would.
                    File.WriteAllBytes(path, Bytes("external"));
                    return Bytes("one");
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("left as found", error.Message, StringComparison.Ordinal);
        Assert.Contains(first, error.Message, StringComparison.Ordinal);
        Assert.Equal("external", Text(first));
        Assert.Equal("what is really on disk", Text(second));
    }

    [Fact]
    public async Task Rollback_still_restores_files_nobody_else_touched()
    {
        var first = Path.Combine(_root, "first.md");
        var second = Path.Combine(_root, "second.md");
        await File.WriteAllBytesAsync(
            second,
            Bytes("what is really on disk"),
            TestContext.Current.CancellationToken);
        var plan = Plan(
            AgentConfigurationFileOperations.FilePlan(
                first,
                [],
                "one",
                DateTimeOffset.UtcNow),
            AgentConfigurationFileOperations.FilePlan(
                second,
                Bytes("a stale preview"),
                "two",
                DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentConfigurationFileOperations.ApplyAsync(
                plan,
                File.ReadAllBytes,
                TestContext.Current.CancellationToken));

        Assert.Contains("changed concurrently", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(first));
        Assert.Equal("what is really on disk", Text(second));
    }

    private static AgentConfigurationPlan Plan(params AgentConfigurationFilePlan[] files) =>
        new("test-agent", files, "preview");

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(string path) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(path));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
