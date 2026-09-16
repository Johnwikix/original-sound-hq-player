// Only the OS boundary is simulated; regression tests compile the production LicenseService.
using System.Collections.Concurrent;

namespace Microsoft.UI.Dispatching
{
    public sealed class TestUiContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();
        public override void Post(SendOrPostCallback callback, object? state) => _work.Add((callback, state));
        public void Run(Task task)
        {
            while (!task.IsCompleted)
                if (_work.TryTake(out var work, 100)) work.Callback(work.State);
            task.GetAwaiter().GetResult();
        }
    }
    public sealed class DispatcherQueue
    {
        public static DispatcherQueue Current { get; } = new();
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        public SynchronizationContext Context { get; } = SynchronizationContext.Current!;
        public bool HasThreadAccess => Environment.CurrentManagedThreadId == _threadId;
        public List<DispatcherQueueTimer> Timers { get; } = [];
        public static DispatcherQueue GetForCurrentThread() => Current;
        public DispatcherQueueTimer CreateTimer() { var timer = new DispatcherQueueTimer(); Timers.Add(timer); return timer; }
    }
    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool Running { get; private set; }
        public event Action<DispatcherQueueTimer, object>? Tick;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Fire() { if (Running) Tick?.Invoke(this, new object()); }
    }
}

namespace CommunityToolkit.WinUI
{
    public static class DispatcherQueueExtensions
    {
        public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Func<Task> callback)
        {
            if (queue.HasThreadAccess) return callback();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Context.Post(async _ =>
            {
                try { await callback(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }, null);
            return completion.Task;
        }
    }
}

namespace Windows.ApplicationModel
{
    public enum PackageSignatureKind { Store, Developer }
    public sealed class Package
    {
        public static Package Current { get; } = new();
        public PackageSignatureKind SignatureKind { get; set; } = PackageSignatureKind.Store;
    }
}

namespace Windows.Services.Store
{
    public sealed class StoreAppLicense
    {
        public bool IsActive { get; init; }
        public bool IsTrial { get; init; }
        public DateTimeOffset ExpirationDate { get; init; }
    }
    public sealed class StoreProduct { public string StoreId => "app"; }
    public sealed class StoreProductResult { public StoreProduct Product { get; } = new(); }
    public sealed class StorePurchaseResult { public string Status => "Succeeded"; public Exception? ExtendedError => null; }
    public sealed class StoreContext
    {
        public static StoreContext Current { get; set; } = new();
        public static StoreContext GetDefault() => Current;
        public bool WindowInitialized { get; set; }
        public int Queries { get; private set; }
        public int Subscribers => OfflineLicensesChanged?.GetInvocationList().Length ?? 0;
        public Func<Task<StoreAppLicense>> Query { get; set; } = () => throw new Exception("Unexpected query");
        public Action? Purchase { get; set; }
        public event Action<StoreContext, object>? OfflineLicensesChanged;
        public void RaiseChanged() => OfflineLicensesChanged?.Invoke(this, new object());
        public Task<StoreAppLicense> GetAppLicenseAsync()
        {
            if (!WindowInitialized) throw new Exception("Missing HWND");
            if (!Microsoft.UI.Dispatching.DispatcherQueue.Current.HasThreadAccess) throw new Exception("Not on UI thread");
            Queries++;
            return Query();
        }
        public Task<StoreProductResult> GetStoreProductForCurrentAppAsync() => Task.FromResult(new StoreProductResult());
        public Task<StorePurchaseResult> RequestPurchaseAsync(string id)
        {
            Purchase?.Invoke();
            return Task.FromResult(new StorePurchaseResult());
        }
    }
}

namespace WinRT.Interop
{
    public static class WindowNative { public static nint GetWindowHandle(object window) => 1; }
    public static class InitializeWithWindow
    {
        public static void Initialize(Windows.Services.Store.StoreContext context, nint hwnd) => context.WindowInitialized = hwnd != 0;
    }
}
namespace Windows.System
{
    public static class Launcher { public static Task<bool> LaunchUriAsync(Uri uri) => Task.FromResult(true); }
}
namespace WinUIMusicPlayer
{
    public static class App { public static object MainWindow { get; } = new(); }
}
