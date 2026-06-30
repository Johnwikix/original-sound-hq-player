using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Helper;

public static partial class WorkingSetCompressor
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_EX2 ppsmemCounters, uint cb);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public UIntPtr SharedCommitUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    public static long GetPrivateWorkingSet()
    {
        var counters = new PROCESS_MEMORY_COUNTERS_EX2();
        counters.cb = (uint)Unsafe.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>();
        if (GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.cb))
            return (long)counters.PrivateWorkingSetSize;
        return 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize,
        uint flags);

    public static void TrimSelf()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true);
        GC.WaitForPendingFinalizers();

        var hProcess = Process.GetCurrentProcess().Handle;
        var minusOne = new IntPtr(-1);

        if (!EmptyWorkingSet(hProcess))
            SetProcessWorkingSetSizeEx(hProcess, minusOne, IntPtr.Zero, 0);
    }

    public static Task TrimSelfAsync()
    {
        return Task.Run(() =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            GC.WaitForPendingFinalizers();

            var hProcess = Process.GetCurrentProcess().Handle;
            var minusOne = new IntPtr(-1);

            if (!EmptyWorkingSet(hProcess))
                SetProcessWorkingSetSizeEx(hProcess, minusOne, IntPtr.Zero, 0);
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