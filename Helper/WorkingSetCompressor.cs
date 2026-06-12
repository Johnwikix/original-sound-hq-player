using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Helper;

public static partial class WorkingSetCompressor
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize,
        uint flags);

    public static void TrimSelf()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

        var hProcess = Process.GetCurrentProcess().Handle;
        var minusOne = new IntPtr(-1);

        if (!EmptyWorkingSet(hProcess))
            SetProcessWorkingSetSizeEx(hProcess, minusOne, minusOne, 0);
    }

    public static Task TrimSelfAsync()
    {
        return Task.Run(() =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

            var hProcess = Process.GetCurrentProcess().Handle;
            var minusOne = new IntPtr(-1);

            if (!EmptyWorkingSet(hProcess))
                SetProcessWorkingSetSizeEx(hProcess, minusOne, minusOne, 0);
        });
    }

    public static bool TrimProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return EmptyWorkingSet(process.Handle);
        }
        catch
        {            
            return false;
        }
    }
}