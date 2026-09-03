using System.Runtime.InteropServices;
using System.Text;

namespace LocalAi.Contracts;

/// <summary>
/// Which encoding turns a child process's redirected bytes back into text.
///
/// .NET's default answer is <see cref="Console.OutputEncoding"/> — the parent's setting for its
/// own output, which says nothing about what the child wrote. Three entry points set that to
/// UTF-8 so their own output is UTF-8, and every child was then read as UTF-8 too. On a Russian
/// Windows a console-mode program writes code page 866, so the NuGet message a person needs in
/// order to fix their environment arrived as "&#xFFFD; &#xFFFD;&#xFFFD;&#x8949;" (#292).
///
/// So every reader names its encoding, and the name says which child it is for. There is no one
/// right answer: <c>git</c> writes UTF-8 whatever the machine's code page is, while a program
/// that follows the console writes the console's.
/// </summary>
public static class ChildProcessText
{
    /// <summary>
    /// For a child that follows the console: the .NET SDK, MSBuild, NuGet, winget, ollama.
    ///
    /// This is what <see cref="Console.OutputEncoding"/> answers in a process that has not
    /// overridden it — the console's output code page when there is a console, and the OEM code
    /// page when there is not, which is the case under a Git hook or an MCP server. Asked of the
    /// operating system each time rather than cached, because a caller may change the console's
    /// code page and the child that follows it will change with it.
    /// </summary>
    public static Encoding ConsoleEncoding
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return Encoding.UTF8;
            }

            EnsureCodePagesRegistered();
            var codePage = NativeMethods.GetConsoleOutputCP();
            if (codePage == 0)
            {
                codePage = NativeMethods.GetOEMCP();
            }

            try
            {
                return Encoding.GetEncoding((int)codePage);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException)
            {
                // A code page the runtime cannot produce is not worth failing a sync over; the
                // text is then read as UTF-8, which is what happened before any of this.
                return Encoding.UTF8;
            }
        }
    }

    /// <summary>
    /// For a child that writes UTF-8 whatever the machine's code page is: Git, this toolkit's
    /// own executables — which say so through <see cref="ConsoleOutputText"/> before they print
    /// anything — and a language server, whose protocol fixes the encoding.
    ///
    /// Stated rather than inherited. On a machine where nothing had overridden the console
    /// encoding, a Git path with non-ASCII in it was being decoded as the OEM code page: the
    /// same defect pointing the other way, and one nobody had noticed because the entry points
    /// happened to set UTF-8 first.
    /// </summary>
    public static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static int registered;

    /// <summary>
    /// Code pages beyond the handful .NET ships with are opt-in, and 866 is one of them. Without
    /// this, asking for it throws and the message stays unreadable.
    /// </summary>
    private static void EnsureCodePagesRegistered()
    {
        if (Interlocked.Exchange(ref registered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetConsoleOutputCP();

        [DllImport("kernel32.dll")]
        internal static extern uint GetOEMCP();
    }
}
