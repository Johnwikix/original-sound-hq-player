using System.Runtime.InteropServices;

namespace HomePolish.Diagnostics;

internal sealed record VideoMemoryAdapter(int Index, ulong LocalUsage, ulong NonLocalUsage, ulong LocalBudget, string Status);

// Independent check of process-local video memory. PDH Dedicated Usage can drift on affected drivers/Windows versions.
internal static class VideoMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Info { public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation; }
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Enumerate(nint factory, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Query(nint adapter, uint node, uint segment, out Info info);

    private static T Method<T>(nint instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    internal static List<VideoMemoryAdapter> Read()
    {
        var result = new List<VideoMemoryAdapter>();
        Guid factoryId = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
        int status = CreateDXGIFactory1(in factoryId, out nint factory);
        if (status < 0) { result.Add(new(-1, 0, 0, 0, $"Factory=0x{status:X8}")); return result; }
        try
        {
            var enumerate = Method<Enumerate>(factory, 12); // IDXGIFactory1::EnumAdapters1
            for (uint i = 0; i < 16; i++)
            {
                status = enumerate(factory, i, out nint adapter);
                if (status == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                if (status < 0) { result.Add(new((int)i, 0, 0, 0, $"Enum=0x{status:X8}")); break; }
                try
                {
                    Guid adapterId = new("645967A4-1392-4310-A798-8053CE3E93FD");
                    status = Marshal.QueryInterface(adapter, in adapterId, out nint adapter3);
                    if (status < 0) { result.Add(new((int)i, 0, 0, 0, $"QI=0x{status:X8}")); continue; }
                    try
                    {
                        var query = Method<Query>(adapter3, 14); // IDXGIAdapter3::QueryVideoMemoryInfo
                        int localStatus = query(adapter3, 0, 0, out Info local);
                        int nonLocalStatus = query(adapter3, 0, 1, out Info nonLocal);
                        result.Add(new((int)i, local.CurrentUsage, nonLocal.CurrentUsage, local.Budget,
                            localStatus >= 0 && nonLocalStatus >= 0 ? "OK" : $"Query=0x{localStatus:X8}/0x{nonLocalStatus:X8}"));
                    }
                    finally { Marshal.Release(adapter3); }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
}
