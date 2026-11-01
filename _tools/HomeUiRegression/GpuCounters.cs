using System.Runtime.InteropServices;

namespace HomePolish.Diagnostics;

internal sealed record GpuCounterValue(string Counter, string Instance, uint Status, long Bytes);
internal sealed record GpuSample(long? Dedicated, long? Shared, long? Committed, string Status, List<GpuCounterValue>? Values = null);

internal static class GpuCounters
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, out uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);

    internal static GpuSample Read()
    {
        uint error = PdhOpenQuery(null, 0, out nint query);
        if (error != 0) return new(null, null, null, $"PdhOpenQuery=0x{error:X8}");
        try
        {
            string prefix = $"\\GPU Process Memory(pid_{Environment.ProcessId}_*)\\";
            var counters = new List<nint>();
            foreach (string name in new[] { "Dedicated Usage", "Shared Usage", "Total Committed" })
            {
                error = PdhAddEnglishCounter(query, prefix + name, 0, out nint counter);
                if (error != 0) return new(null, null, null, $"{name}=0x{error:X8}");
                counters.Add(counter);
            }
            error = PdhCollectQueryData(query);
            if (error != 0) return new(null, null, null, $"PdhCollect=0x{error:X8}");
            long?[] values = new long?[3];
            var rawValues = new List<GpuCounterValue>();
            for (int i = 0; i < counters.Count; i++)
            {
                uint size = 0;
                PdhGetFormattedCounterArray(counters[i], 0x400, ref size, out _, 0);
                if (size == 0) continue;
                nint buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    error = PdhGetFormattedCounterArray(counters[i], 0x400, ref size, out uint count, buffer);
                    if (error != 0) continue;
                    long sum = 0;
                    int valid = 0;
                    for (int j = 0; j < count; j++)
                    {
                        nint item = buffer + j * 24;
                        uint status = (uint)Marshal.ReadInt32(item, 8);
                        long bytes = Marshal.ReadInt64(item, 16);
                        rawValues.Add(new(new[] { "Dedicated", "Shared", "Committed" }[i], Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "", status, bytes));
                        if (status <= 1) { sum += bytes; valid++; }
                    }
                    values[i] = valid > 0 ? sum : null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return new(values[0], values[1], values[2], values.All(v => v.HasValue) ? "OK" : "No matching GPU counter instance", rawValues);
        }
        finally { PdhCloseQuery(query); }
    }
}
