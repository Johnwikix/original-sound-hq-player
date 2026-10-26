using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length != 2) throw new ArgumentException("Usage: TraceAnalysis <input.nettrace> <output.json>");
string tracePath = Path.GetFullPath(args[0]);
string etlxPath = Path.ChangeExtension(tracePath, ".etlx");
using (var source = new EventPipeEventSource(tracePath))
    TraceLog.CreateFromEventTraceLogFile(source, etlxPath, new TraceLogOptions());
using var log = new TraceLog(etlxPath);
string phase = "outside-measurements";
var phases = new Dictionary<string, Phase>();
var starts = new List<object>();
int allocationsWithoutStacks = 0;
int allocationSamples = 0;
foreach (TraceEvent data in log.Events)
{
    if (data.ProviderName == "MusicPlayer-DrawPathProbe")
    {
        if ((int)data.ID == 1)
        {
            phase = data.PayloadString(0);
            starts.Add(new { Name = phase, data.TimeStampRelativeMSec, data.ThreadID, data.ProcessID });
        }
        else if ((int)data.ID == 2) phase = "outside-measurements";
        continue;
    }
    if (!phases.TryGetValue(phase, out var stats)) phases.Add(phase, stats = new Phase());
    if (data is GCAllocationTickTraceData allocation)
    {
        allocationSamples++;
        long bytes = allocation.AllocationAmount64 > 0 ? allocation.AllocationAmount64 : allocation.AllocationAmount;
        stats.AllocationSamples++;
        stats.SampledBytes += bytes;
        Add(stats.Types, allocation.TypeName ?? "<unknown>", bytes);
        var stack = data.CallStack();
        if (stack is null) allocationsWithoutStacks++;
        string[] frames = Frames(stack);
        Add(stats.AllocationStacks, string.Join(" <- ", frames), bytes);
        foreach (string method in frames.Distinct()) Add(stats.AllocationMethods, method, bytes);
    }
    else if (data.ProviderName == "Microsoft-DotNETCore-SampleProfiler")
    {
        stats.CpuSamples++;
        Add(stats.CpuStacks, string.Join(" <- ", Frames(data.CallStack())), 1);
    }
}
var result = new
{
    Input = tracePath,
    Note = "GCAllocationTick sampled bytes are approximate attributed allocation intervals, not exact per-type totals. CPU samples describe sampled managed/native stacks, not GPU elapsed time. Exact phase totals are recorded independently by DrawPathProbe.",
    AllocationSamples = allocationSamples,
    AllocationsWithoutStacks = allocationsWithoutStacks,
    PhaseStarts = starts,
    Phases = phases.Select(pair => new
    {
        Name = pair.Key, pair.Value.AllocationSamples, pair.Value.SampledBytes, pair.Value.CpuSamples,
        Types = Top(pair.Value.Types, 20),
        AllocationMethods = Top(pair.Value.AllocationMethods, 30),
        AllocationStacks = Top(pair.Value.AllocationStacks, 20),
        CpuStacks = Top(pair.Value.CpuStacks, 12)
    })
};
File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Analyzed {allocationSamples} allocation samples; {allocationsWithoutStacks} without stacks; {starts.Count} measured phase starts.");

static void Add(Dictionary<string, long> items, string key, long value)
    => items[key] = items.GetValueOrDefault(key) + value;
static object[] Top(Dictionary<string, long> items, int count)
    => items.OrderByDescending(pair => pair.Value).Take(count).Select(pair => (object)new { Name = pair.Key, Value = pair.Value }).ToArray();
static string[] Frames(TraceCallStack? stack)
{
    var result = new List<string>();
    for (int depth = 0; stack is not null && depth < 48; depth++, stack = stack.Caller)
        result.Add(stack.CodeAddress.FullMethodName);
    return result.ToArray();
}

/// <summary>Collects sampled attribution for one independently marked probe phase.</summary>
internal sealed class Phase
{
    internal int AllocationSamples;
    internal long SampledBytes;
    internal int CpuSamples;
    internal readonly Dictionary<string, long> Types = [];
    internal readonly Dictionary<string, long> AllocationMethods = [];
    internal readonly Dictionary<string, long> AllocationStacks = [];
    internal readonly Dictionary<string, long> CpuStacks = [];
}
