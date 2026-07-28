using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeSearch.Core.Search;

/// <summary>
/// Result of an unload, measured in process working set - the number that actually moves when
/// memory is released, and the one a user can confirm in Task Manager.
/// </summary>
public sealed record UnloadReport(int IndexesDropped, int FreedMb, int RemainingMb, bool WorkingSetTrimmed);

internal static partial class NativeMemory
{
    /// <summary>
    /// Asks Windows to trim this process's working set.
    ///
    /// Freeing the managed heap is not enough to make memory come back: the .NET GC decommits
    /// lazily, so a process that has just released 675MB of vectors still shows ~700MB resident
    /// until the OS decides otherwise. Passing (SIZE_T)-1 for both bounds moves the pages to the
    /// standby list immediately, which is what "release the memory now" has to mean for it to be
    /// an honest claim.
    ///
    /// Pages are reclaimable, not discarded - anything still live faults back in on next touch.
    /// </summary>
    public static bool TrimWorkingSet()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Nothing to do: on Linux and macOS the GC's own decommit is what governs RSS.
            return false;
        }

        try
        {
            return SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumBytes, IntPtr maximumBytes);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();
}
