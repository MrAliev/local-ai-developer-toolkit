using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;

namespace LocalAi.Installer;

/// <summary>
/// The one part of a window the palette cannot reach.
///
/// The caption, the border and the system buttons are drawn by the desktop window manager, not
/// by WPF, so a window whose content went dark keeps a white title bar until the manager is
/// told otherwise. It is told through an undocumented-but-stable window attribute rather than
/// by drawing the caption ourselves, which would mean reimplementing minimise, maximise, close,
/// snap layouts and the double-click-to-maximise the manager gives for free.
///
/// Applied when the handle first exists and again on every swap: the attribute belongs to the
/// window, and a window created before a swap would otherwise keep the caption it was born with.
/// Nothing here may fail a run — a caption that stays light is a blemish, not a reason to refuse
/// to install.
/// </summary>
public static class DarkCaption
{
    /// <summary>
    /// DWMWA_USE_IMMERSIVE_DARK_MODE. Value 20 since Windows 10 build 18985; the earlier
    /// builds used 19, and asking with the wrong one simply returns a failure code.
    /// </summary>
    private const int UseImmersiveDarkMode = 20;

    private const int UseImmersiveDarkModeBefore19H1 = 19;

    /// <summary>
    /// Keeps one window's caption in step with the palette for as long as it is open.
    ///
    /// Two moments, because neither covers the other: the handle does not exist while the
    /// window is being constructed, and a window already open when somebody switches has a
    /// handle but no reason to look again.
    /// </summary>
    public static void Follow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var themes = App.Themes;
        window.SourceInitialized += (_, _) => Apply(window, App.Themes?.IsDark ?? false);
        if (themes is null)
        {
            return;
        }

        void OnApplied(object? sender, bool dark) => Apply(window, dark);
        themes.Applied += OnApplied;
        window.Closed += (_, _) => themes.Applied -= OnApplied;
    }

    public static void Apply(Window window, bool dark)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            // Before the handle exists there is nothing to tell; the window applies this again
            // from OnSourceInitialized.
            return;
        }

        Apply(handle, dark);
    }

    [SupportedOSPlatform("windows")]
    private static void Apply(IntPtr handle, bool dark)
    {
        var value = dark ? 1 : 0;
        try
        {
            if (NativeMethods.DwmSetWindowAttribute(
                    handle,
                    UseImmersiveDarkMode,
                    ref value,
                    sizeof(int)) != 0)
            {
                NativeMethods.DwmSetWindowAttribute(
                    handle,
                    UseImmersiveDarkModeBefore19H1,
                    ref value,
                    sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // A Windows without the desktop window manager keeps its own caption, which is
            // the whole of the consequence.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int value,
            int size);
    }
}
