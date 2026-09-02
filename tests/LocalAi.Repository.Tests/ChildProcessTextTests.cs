using System.Diagnostics;
using System.Text;
using LocalAi.Contracts;

namespace LocalAi.Repository.Tests;

/// <summary>
/// A child process writes bytes; something has to decide which encoding turns them back into
/// text. .NET's answer, when nobody says, is <see cref="Console.OutputEncoding"/> — the parent's
/// setting for its own output, which says nothing about what the child wrote.
///
/// Three entry points set <c>Console.OutputEncoding = UTF8</c> so their own output is UTF-8.
/// That silently made every child's output be decoded as UTF-8 too, and a console-mode program
/// on a Russian Windows writes code page 866, so the NuGet message a person needs in order to
/// fix their environment arrived unreadable (#292).
///
/// What these tests pin is that naming an encoding is honoured end to end. Which code page a
/// given child actually writes is a judgement stated in <see cref="ChildProcessText"/> and
/// backed by a reproduction, not by a test: it depends on whether the run has a console, and a
/// test process always has one.
/// </summary>
public sealed class ChildProcessTextTests
{
    /// <summary>
    /// 866 rather than whatever this machine happens to use. The first version of this test
    /// wrote the file in the machine's own console code page, which on the machine it was
    /// written on was already UTF-8 — so it passed with the fix removed, proving nothing.
    /// </summary>
    private const int RussianOem = 866;

    [Fact]
    public async Task A_child_writing_another_code_page_is_read_back_intact()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "OEM code pages are a Windows notion.");

        const string written = "В вашей конфигурации определены 2 источника пакетов";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(RussianOem);
        var file = Path.Combine(Path.GetTempPath(), $"localai-cp-{Guid.NewGuid():N}.txt");
        await File.WriteAllBytesAsync(
            file,
            oem.GetBytes(written),
            TestContext.Current.CancellationToken);

        // A parent that has chosen UTF-8 for its own output, which is the state every entry
        // point puts the process in before any of this runs.
        var original = Console.OutputEncoding;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = oem,
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("type");
            start.ArgumentList.Add(file);

            using var process = Process.Start(start)!;
            var text = await process.StandardOutput.ReadToEndAsync(
                TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Contains(written, text, StringComparison.Ordinal);
        }
        finally
        {
            Console.OutputEncoding = original;
            File.Delete(file);
        }
    }

    /// <summary>
    /// The code page the report named has to be obtainable at all. .NET ships a handful of
    /// encodings and 866 is not one of them, so without registering the provider the lookup
    /// throws and the message stays as unreadable as it was.
    /// </summary>
    [Fact]
    public void The_console_encoding_is_obtainable_without_the_caller_registering_anything()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "OEM code pages are a Windows notion.");

        Assert.NotNull(ChildProcessText.ConsoleEncoding);
        Assert.Equal(RussianOem, Encoding.GetEncoding(RussianOem).CodePage);
    }

    /// <summary>
    /// Git, this toolkit's own executables and a language server all write UTF-8 whatever the
    /// machine's code page is, so their readers say so rather than inheriting a console setting
    /// — on a machine that had not overridden it, a path with non-ASCII in it would otherwise
    /// have been decoded as the OEM code page.
    /// </summary>
    [Fact]
    public void The_utf8_children_are_read_as_utf8()
    {
        Assert.Equal(Encoding.UTF8.CodePage, ChildProcessText.Utf8.CodePage);
    }
}
