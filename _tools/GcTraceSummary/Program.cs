using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

// Reads recorded EventPipe data. Suspension duration is measured separately from GC duration.
if (args.Length < 2) return 2;
int pid = int.Parse(args[1]);
using var source = new EventPipeEventSource(args[0]);
var pauses = new List<double>();
var generations = new int[3];
var types = new Dictionary<string, long>();
var events = new Dictionary<string, int>();
double suspendedAt = -1, totalAllocated = 0;
long? lastHeap = null;
source.Dynamic.All += e => { if(e.ProcessID == pid) events[e.EventName] = events.GetValueOrDefault(e.EventName) + 1; };
source.Clr.GCStart += e => { if(e.ProcessID == pid && e.Depth is >= 0 and <= 2) generations[e.Depth]++; };
source.Clr.GCSuspendEEStart += e => { if(e.ProcessID == pid) suspendedAt = e.TimeStampRelativeMSec; };
source.Clr.GCRestartEEStop += e => { if(e.ProcessID == pid && suspendedAt >= 0) { pauses.Add(e.TimeStampRelativeMSec - suspendedAt); suspendedAt = -1; } };
source.Clr.GCAllocationTick += e =>
{
    if(e.ProcessID != pid) return;
    long bytes = e.AllocationAmount64 > 0 ? e.AllocationAmount64 : e.AllocationAmount;
    string type = (string.IsNullOrEmpty(e.TypeName) || e.TypeName == "NULL") ? "(type unavailable)" : e.TypeName;
    types[type] = types.GetValueOrDefault(type) + bytes; totalAllocated += bytes;
};
source.Clr.GCHeapStats += e => { if(e.ProcessID == pid) lastHeap = e.GenerationSize0 + e.GenerationSize1 + e.GenerationSize2 + e.GenerationSize3; };
source.Process();
Console.WriteLine(JsonSerializer.Serialize(new { pid, durationMs = (source.SessionEndTime - source.SessionStartTime).TotalMilliseconds,
    gcByGeneration = generations, suspensionCount = pauses.Count, totalSuspensionMs = pauses.Sum(), maxSuspensionMs = pauses.Count == 0 ? 0 : pauses.Max(),
    sampledAllocatedBytes = totalAllocated, lastHeapBytes = lastHeap,
    allocationTypes = types.OrderByDescending(x => x.Value).Take(20), events }, new JsonSerializerOptions { WriteIndented = true }));
return 0;
