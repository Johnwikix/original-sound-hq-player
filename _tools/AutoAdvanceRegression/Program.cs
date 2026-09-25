using WinUIMusicPlayer.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var gate = new AutoAdvanceGate();
var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int advances = 0;

async Task RequestAdvanceAsync()
{
    if (!gate.Request()) return;
    do
    {
        advances++;
        if (advances == 1) await releaseFirst.Task;
    }
    while (gate.Complete());
}

Task first = RequestAdvanceAsync();
Check(advances == 1 && !first.IsCompleted, "first transition did not stay in flight");
await RequestAdvanceAsync();
await RequestAdvanceAsync();
Check(advances == 1, "second end started a concurrent transition");
releaseFirst.SetResult();
await first;
Check(advances == 2, "second end was discarded before the first transition completed");
await RequestAdvanceAsync();
Check(advances == 3, "gate did not reopen after pending end was consumed");

var stopping = new AutoAdvanceGate();
Check(stopping.Request(), "initial request rejected");
Check(!stopping.Request(), "pending request started concurrently");
stopping.Cancel();
Check(stopping.Request() && !stopping.Complete(), "cancelled transition left a stale request");
Console.WriteLine("Auto advance regressions: pending end runs after the in-flight transition; duplicates coalesce; cancellation clears pending work.");
