using System.Collections.Concurrent;

namespace Microsoft.UI.Dispatching;

// Deterministic dispatcher boundary, not a substitute for the WinUI integration suite.
public sealed class DispatcherQueue
{
    [ThreadStatic] private static DispatcherQueue? current;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> pending = new();
    public bool Reject { get; set; }
    public bool HasThreadAccess => Environment.CurrentManagedThreadId == owner;
    public int PendingCount => pending.Count;
    public static DispatcherQueue? GetForCurrentThread() => current;
    public static DispatcherQueue Install() => current = new();
    public bool TryEnqueue(Action action)
    {
        if (Reject) return false;
        pending.Enqueue(action);
        return true;
    }
    public void Drain()
    {
        if (!HasThreadAccess) throw new InvalidOperationException("Wrong test dispatcher thread.");
        while (pending.TryDequeue(out var action)) action();
    }
}
