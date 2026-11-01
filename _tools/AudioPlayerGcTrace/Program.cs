using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length != 2) throw new ArgumentException("AudioPlayerGcTrace <input.nettrace> <output.json>");
string input = Path.GetFullPath(args[0]);
string etlx = Path.ChangeExtension(input, ".etlx");
using (var source = new EventPipeEventSource(input))
    TraceLog.CreateFromEventTraceLogFile(source, etlx, new TraceLogOptions());
using var log = new TraceLog(etlx);
var collections = new List<object>();
var phases = new List<object>();
string phase = "unmarked";
var starts = new Dictionary<int, (double Ms, int Gen, string Reason, string Type)>();
var suspensionWindows = new List<object>();
var types = new Dictionary<string, long>();
var stacks = new Dictionary<string, long>();
var events = new Dictionary<string, long>();
double? suspensionStart = null, suspended = null, restarting = null;
double totalSuspension = 0, maxSuspension = 0;
long sampledAllocation = 0;
int allocations = 0, missingStacks = 0, unresolvedTypes = 0;
foreach (TraceEvent data in log.Events)
{
    if (data.ProviderName == "HomePolishProbe")
    {
        phase = data.PayloadNames.Length > 0 ? data.PayloadValue(0)?.ToString() ?? "unknown" : "unknown";
        phases.Add(new { Ms = data.TimeStampRelativeMSec, Phase = phase });
        continue;
    }
    if (data.ProviderName != "Microsoft-Windows-DotNETRuntime") continue;
    events[data.EventName] = events.GetValueOrDefault(data.EventName) + 1;
    double ms = data.TimeStampRelativeMSec;
    if (data is GCAllocationTickTraceData allocation)
    {
        long bytes = allocation.AllocationAmount64 > 0 ? allocation.AllocationAmount64 : allocation.AllocationAmount;
        allocations++;
        sampledAllocation += bytes;
        string type = allocation.TypeName ?? "<unknown>";
        if (type is "NULL" or "<unknown>" or "")
        {
            unresolvedTypes++;
            type = "<unresolved>";
        }
        types[type] = types.GetValueOrDefault(type) + bytes;
        var frames = new List<string>();
        var stack = data.CallStack();
        if (stack == null) missingStacks++;
        for (int depth = 0; stack != null && depth < 40; depth++, stack = stack.Caller)
            frames.Add(stack.CodeAddress.FullMethodName);
        string key = string.Join(" <- ", frames);
        stacks[key] = stacks.GetValueOrDefault(key) + bytes;
    }
    else if (data is GCStartTraceData start)
        starts[start.Count] = (ms, start.Depth, start.Reason.ToString(), start.Type.ToString());
    else if (data is GCEndTraceData stop && starts.Remove(stop.Count, out var startInfo))
        collections.Add(new { Index = stop.Count, StartMs = startInfo.Ms, EndMs = ms, Generation = startInfo.Gen,
            startInfo.Reason, startInfo.Type, CollectionDurationMs = ms - startInfo.Ms });
    // Suspension intervals include thread rendezvous and restart. Background GC
    // Start->End is deliberately recorded only as collection duration, never STW.
    if (data.EventName == "GC/SuspendEEStart")
    {
        suspensionStart = ms;
        suspended = restarting = null;
    }
    else if (data.EventName == "GC/SuspendEEStop") suspended = ms;
    else if (data.EventName == "GC/RestartEEStart") restarting = ms;
    else if (data.EventName == "GC/RestartEEStop" && suspensionStart.HasValue)
    {
        double duration = ms - suspensionStart.Value;
        totalSuspension += duration;
        maxSuspension = Math.Max(maxSuspension, duration);
        suspensionWindows.Add(new { Phase = phase, StartMs = suspensionStart.Value, EndMs = ms, DurationMs = duration,
            FullySuspendedMs = suspended.HasValue && restarting.HasValue ? restarting.Value - suspended.Value : (double?)null });
        suspensionStart = null;
    }
}
static object[] Top(Dictionary<string, long> values, int count) => values.OrderByDescending(x => x.Value)
    .Take(count).Select(x => (object)new { Name = x.Key, SampledBytes = x.Value }).ToArray();
File.WriteAllText(args[1], JsonSerializer.Serialize(new { Input = input,
    Note = "AllocationTick attributes sampled intervals, not exact per-type totals. Trace instrumentation is excluded from the process-memory comparison. Suspension windows use SuspendEEStart->RestartEEStop; collection duration includes concurrent background work.",
    AllocationSamples = allocations, SampledAllocationBytes = sampledAllocation, AllocationsWithoutStacks = missingStacks,
    AllocationsWithoutTypeNames = unresolvedTypes,
    SuspensionCount = suspensionWindows.Count, TotalSuspensionMs = totalSuspension, MaxSuspensionMs = maxSuspension,
    Phases = phases, Collections = collections, SuspensionWindows = suspensionWindows, AllocationTypes = Top(types, 20),
    AllocationStacks = Top(stacks, 20), EventCounts = events }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{collections.Count} collections, {suspensionWindows.Count} suspension windows ({totalSuspension:F3} ms), {allocations} allocation samples");
