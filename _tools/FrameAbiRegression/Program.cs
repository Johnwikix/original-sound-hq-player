using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinRT;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new RegressionApp();
        });
    }
}

internal sealed partial class RegressionApp : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider _winui = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    private readonly IXamlMetadataProvider _controls = new AnimatedWin2dControls.AnimatedWin2dControls_XamlTypeInfo.XamlMetaDataProvider();
    public IXamlType GetXamlType(Type type) => _controls.GetXamlType(type) ?? _winui.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _controls.GetXamlType(name) ?? _winui.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private static int _checks;
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string result = Path.Combine(AppContext.BaseDirectory, "result.txt");
        File.WriteAllText(result, "RUNNING");
        var canvas = new CanvasAnimatedControl();
        var window = new Window { Content = canvas, Title = "Frame ABI regression" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 480));
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            await NativeBoundaryChecks.RunAsync();
            await CheckLiveUpdateAsync(canvas, window);
            await CheckLiveDrawAsync(canvas);
            await canvas.RunOnGameLoopThreadAsync(() => NativeBoundaryChecks.CheckDraw(canvas));
            await CheckClockAsync();
            await CheckAbandonedNativeHandlersAsync(canvas);
            await CheckHostsAsync(window);
            File.WriteAllText(result, $"PASS: {_checks} checks; raw COM invoke/timing/HRESULT, registration failure, late callbacks, self/concurrent unsubscription, warmed callback allocation, real Rendering/Update equality, pause/resume, reattach, host unload/reload/shutdown.");
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(result, "FAIL: " + error);
        }
        finally
        {
            canvas.Paused = true;
            canvas.RemoveFromVisualTree();
            window.Close();
            Exit();
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string message)
    {
        for (int i = 0; i < 250; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(message);
    }

    private static async Task CheckLiveUpdateAsync(CanvasAnimatedControl canvas, Window window)
    {
        long elapsed = 0;
        int managed = 0, raw = 0, mismatches = 0, wrongThread = 0;
        void Managed(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
        {
            elapsed = args.Timing.ElapsedTime.Ticks;
            Interlocked.Increment(ref managed);
        }
        canvas.Update += Managed;
        using var subscription = FrameEventSubscription.SubscribeUpdate(canvas, delta =>
        {
            if (delta.Ticks != elapsed) Interlocked.Increment(ref mismatches);
            if (!canvas.HasGameLoopThreadAccess) Interlocked.Increment(ref wrongThread);
            Interlocked.Increment(ref raw);
        });
        window.Activate();
        await UntilAsync(() => Volatile.Read(ref raw) >= 10, "Real Win2D Update did not arrive");
        Check(mismatches == 0 && wrongThread == 0, "ABI timing/thread differs from the managed event");
        canvas.Paused = true;
        await canvas.RunOnGameLoopThreadAsync(() => { });
        int paused = Volatile.Read(ref raw);
        await Task.Delay(80);
        Check(raw == paused, "Paused canvas still updates");
        canvas.Paused = false;
        await UntilAsync(() => Volatile.Read(ref raw) > paused + 2, "Resume did not restore Update");
        subscription.Dispose();
        int stopped = Volatile.Read(ref raw), before = Volatile.Read(ref managed);
        await UntilAsync(() => Volatile.Read(ref managed) > before + 2, "Managed control stopped with ABI subscription");
        Check(raw == stopped, "ABI Update arrived after Dispose returned");
        using (var replacement = FrameEventSubscription.SubscribeUpdate(canvas, _ => Interlocked.Increment(ref raw)))
            await UntilAsync(() => Volatile.Read(ref raw) > stopped + 2, "Resubscription did not restore callbacks");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool exited = false;
        using (var blocking = FrameEventSubscription.SubscribeUpdate(canvas, _ =>
        {
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("Native frame was not released");
            exited = true;
        }))
        {
            await UntilAsync(() => entered.IsSet, "Blocking native Update did not enter");
            var releaser = Task.Run(async () => { await Task.Delay(100); release.Set(); });
            try
            {
                blocking.Dispose();
                Check(exited, "Dispose did not drain the real Win2D callback");
            }
            finally { release.Set(); }
            await releaser;
        }
        entered.Reset();
        release.Reset();
        FrameEventSubscription? self = null;
        int selfCalls = 0;
        self = FrameEventSubscription.SubscribeUpdate(canvas, _ =>
        {
            if (self is null) return;
            self.Dispose();
            Interlocked.Increment(ref selfCalls);
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("Self-disposed native frame was not released");
        });
        try
        {
            await UntilAsync(() => entered.IsSet, "Native Update could not unsubscribe itself");
            var releaser = Task.Run(async () => { await Task.Delay(100); release.Set(); });
            try { self.Dispose(); }
            finally { release.Set(); }
            await releaser;
            Check(selfCalls == 1, "Native self/concurrent unsubscription dispatched more than once");
        }
        finally { release.Set(); self.Dispose(); }
        canvas.Update -= Managed;
        canvas.Paused = true;
    }

    private static async Task CheckClockAsync()
    {
        int frames = 0;
        using (var rendering = FrameEventSubscription.SubscribeRendering(() => frames++))
            await UntilAsync(() => frames >= 3, "Real Rendering did not arrive");
        int stopped = frames;
        await Task.Delay(80);
        Check(frames == stopped, "Rendering arrived after unsubscription");
        var first = new Tickable();
        var second = new Tickable();
        first.Tick = () =>
        {
            SharedAnimationClock.Unregister(first);
            SharedAnimationClock.Register(second);
        };
        second.Tick = () => SharedAnimationClock.Unregister(second);
        SharedAnimationClock.Register(first);
        SharedAnimationClock.Register(first);
        try
        {
            await UntilAsync(() => second.Count > 0, "Clock mutation lost the replacement listener");
            Check(first.Count == 1 && second.Count == 1, "Clock duplicated or lost a self-unregistering listener");
            Check(first.Last > TimeSpan.Zero && first.Last <= TimeSpan.FromSeconds(0.1), "Clock delta clamp changed");
            var field = typeof(SharedAnimationClock).GetField("_subscription", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check(field.GetValue(null) is null, "Empty shared clock retained its native subscription");
        }
        finally
        {
            SharedAnimationClock.Unregister(first);
            SharedAnimationClock.Unregister(second);
        }
    }

    private static async Task CheckLiveDrawAsync(CanvasAnimatedControl canvas)
    {
        canvas.Paused = false;
        int frames = 0;
        using var subscription = FrameEventSubscription.SubscribeDraw(canvas, context =>
        {
            context.Clear(Microsoft.UI.Colors.Transparent);
            if (!canvas.HasGameLoopThreadAccess) throw new InvalidOperationException("Draw thread changed");
            Interlocked.Increment(ref frames);
        });
        await UntilAsync(() => Volatile.Read(ref frames) > 4, "Raw Draw did not arrive");
        Check(frames > 4, "Raw Draw did not render real frames");
        subscription.Dispose();
        int stopped = Volatile.Read(ref frames);
        await Task.Delay(80);
        Check(frames == stopped, "Draw arrived after unsubscription returned");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool exited = false;
        using var blocking = FrameEventSubscription.SubscribeDraw(canvas, context =>
        {
            context.Clear(Microsoft.UI.Colors.Transparent);
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("Draw barrier was not released");
            exited = true;
        });
        await UntilAsync(() => entered.IsSet, "Blocking Draw did not enter");
        var releaser = Task.Run(async () => { await Task.Delay(100); release.Set(); });
        try
        {
            blocking.Dispose();
            Check(exited, "Draw unsubscription returned before its callback exited");
        }
        finally { release.Set(); }
        await releaser;
        FrameEventSubscription? self = null;
        int selfCalls = 0;
        self = FrameEventSubscription.SubscribeDraw(canvas, context =>
        {
            if (self is null) return;
            context.Clear(Microsoft.UI.Colors.Transparent);
            self.Dispose();
            Interlocked.Increment(ref selfCalls);
        });
        await UntilAsync(() => Volatile.Read(ref selfCalls) > 0, "Real Draw did not self-unsubscribe");
        await Task.Delay(50);
        Check(selfCalls == 1, "Self-disposed real Draw was invoked again");
        self.Dispose();
        canvas.Paused = true;
    }

    private static async Task CheckHostsAsync(Window window)
    {
        foreach (Control host in new Control[] { new NowPlayingCanvas { EnableAdvancedLyrics = false }, new AdvanceLyricsCanvasControl() })
        {
            var field = host.GetType().GetField("_updateSubscription", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var drawField = host.GetType().GetField("_drawSubscription", BindingFlags.Instance | BindingFlags.NonPublic)!;
            window.Content = host;
            await UntilAsync(() => host.IsLoaded && field.GetValue(host) is not null, "Host did not subscribe");
            Check(drawField.GetValue(host) is not null, "Host did not subscribe raw Draw");
            var original = field.GetValue(host);
            window.Content = new Grid();
            await UntilAsync(() => !host.IsLoaded && field.GetValue(host) is null, "Unloaded host retained Update");
            Check(drawField.GetValue(host) is null, "Unloaded host retained Draw");
            window.Content = host;
            await UntilAsync(() => host.IsLoaded && field.GetValue(host) is not null, "Reload did not subscribe");
            Check(drawField.GetValue(host) is not null, "Reload did not restore Draw");
            Check(!ReferenceEquals(original, field.GetValue(host)), "Reload reused a retired subscription");
            if (host is NowPlayingCanvas combined) combined.PrepareForShutdown();
            else ((AdvanceLyricsCanvasControl)host).PrepareForShutdown();
            Check(field.GetValue(host) is null, "Shutdown retained Update");
            Check(drawField.GetValue(host) is null, "Shutdown retained Draw");
            window.Content = new Grid();
        }
    }

    private static async Task CheckAbandonedNativeHandlersAsync(CanvasAnimatedControl canvas)
    {
        canvas.Paused = false;
        foreach (int kind in new[] { 0, 1, 2 })
        {
            var (owner, handler) = CreateAbandonedNativeHandler(canvas, kind);
            NativeBoundaryChecks.CollectForOwnershipCheck();
            Check(!owner.IsAlive, "Real native source rooted its abandoned subscription");
            await Task.Delay(150);
            NativeBoundaryChecks.CollectForOwnershipCheck();
            Check(!handler.IsAlive, "Real native source retained its orphan handler: " + kind);
        }
        canvas.Paused = true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Handler) CreateAbandonedNativeHandler(CanvasAnimatedControl canvas, int kind)
    {
        var subscription = kind switch
        {
            0 => FrameEventSubscription.SubscribeRendering(() => { }),
            1 => FrameEventSubscription.SubscribeUpdate(canvas, _ => { }),
            _ => FrameEventSubscription.SubscribeDraw(canvas, _ => { })
        };
        var handler = typeof(FrameEventSubscription).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(subscription)!;
        return (new(subscription), new(handler));
    }

    private sealed class Tickable : ISharedTickable
    {
        internal Action? Tick;
        internal int Count;
        internal TimeSpan Last;
        public void OnSharedTick(TimeSpan elapsed)
        {
            Last = elapsed;
            Count++;
            Tick?.Invoke();
        }
    }
}

/// <summary>保留原生 COM 委托、跨线程时序和迟到 Invoke；只替代事件源。</summary>
internal static class NativeBoundaryChecks
{
    private static readonly ConstructorInfo Constructor = typeof(FrameEventSubscription).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
    private static readonly Action<FrameEventSubscription, int, nint> Register = typeof(FrameEventSubscription).GetMethod("Register", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<FrameEventSubscription, int, nint>>();
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static unsafe FrameEventSubscription Subscribe(TestSource source, Action? callback, Action<TimeSpan>? update = null, CanvasDrawHandler? draw = null)
    {
        void* pointer = ComInterfaceMarshaller<ITestSource>.ConvertToUnmanaged(source);
        IObjectReference reference;
        try { reference = ComWrappersSupport.GetObjectReferenceForInterface((nint)pointer); }
        finally { ComInterfaceMarshaller<ITestSource>.Free(pointer); }
        var owner = (FrameEventSubscription)Constructor.Invoke([reference, 7, callback, update, typeof(ITestSource).GUID, false]);
        if (draw is not null) typeof(FrameEventSubscription).GetField("_draw", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, draw);
        string name = draw is not null ? "RawCanvasDrawHandler" : update is null ? "RawRenderingHandler" : "RawCanvasUpdateHandler";
        object handler = Activator.CreateInstance(typeof(FrameEventSubscription).Assembly.GetType("AnimatedWin2dControls.Interop." + name)!, owner)!;
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
        Guid iid = new(draw is not null ? "00ddd999-594e-5c0f-b4a0-52b01171dcd6" : update is null ? "c50898f6-c536-5f47-8583-8b2c2438a13b" : "f3505553-11e9-548c-80b6-33e56d2e673b");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint sink));
        Marshal.Release(unknown);
        try { Register(owner, 6, sink); }
        catch { owner.Dispose(); throw; }
        finally { Marshal.Release(sink); }
        return owner;
    }

    internal static unsafe void CheckDraw(CanvasAnimatedControl canvas)
    {
        using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(canvas, 64, 64);
        using var drawing = target.CreateDrawingSession();
        Guid iid = new("f60afd09-e623-4be0-b750-578aa920b1db");
        Marshal.ThrowExceptionForHR(((IWinRTObject)drawing).NativeObject.TryAs(iid, out nint session));
        var args = new TestDrawArgs { Session = session };
        void* pointer = ComInterfaceMarshaller<ITestDrawArgs>.ConvertToUnmanaged(args);
        try
        {
            using var source = new TestSource { FailRemove = true };
            var subscription = Subscribe(source, null, draw: context => context.Clear(Microsoft.UI.Colors.Transparent));
            RegressionApp.Check(source.Fire((nint)pointer) == 0 && args.Reads == 1, "Draw did not read the real session");
            RegressionApp.Check(source.Fire(0) == unchecked((int)0x80004003), "Null Draw args HRESULT changed");
            args.FailRead = true;
            RegressionApp.Check(source.Fire((nint)pointer) == unchecked((int)0x80004005), "Draw session getter error was ignored");
            args.FailRead = false;
            int references = Marshal.AddRef(session);
            Marshal.Release(session);
            for (int i = 0; i < 1000; i++) source.Fire((nint)pointer);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) source.Fire((nint)pointer);
            RegressionApp.Check(GC.GetAllocatedBytesForCurrentThread() == before, "Raw Draw bridge allocated after warmup");
            int after = Marshal.AddRef(session);
            Marshal.Release(session);
            RegressionApp.Check(after == references, "Draw getter leaked native session references");
            subscription.Dispose();
            RegressionApp.Check(source.Fire(1) == 0, "Late Draw read retired args after remove failure");
            using var selfSource = new TestSource();
            FrameEventSubscription? self = null;
            self = Subscribe(selfSource, null, draw: context => { context.Clear(Microsoft.UI.Colors.Transparent); self!.Dispose(); });
            RegressionApp.Check(selfSource.Fire((nint)pointer) == 0 && selfSource.Removes == 1, "Self-disposed Draw failed");
            self.Dispose();
        }
        finally
        {
            ComInterfaceMarshaller<ITestDrawArgs>.Free(pointer);
            Marshal.Release(session);
        }
    }

    internal static async Task RunAsync()
    {
        using (var source = new TestSource())
        {
            int calls = 0;
            using var subscription = Subscribe(source, () => calls++);
            RegressionApp.Check(source.Fire(1) == 0 && calls == 1, "Rendering projected or dereferenced ignored arguments");
            for (int i = 0; i < 1000; i++) source.Fire(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) source.Fire(0);
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            RegressionApp.Check(allocation == 0, $"Warmed Rendering callbacks allocated {allocation} bytes");
            subscription.Dispose();
            subscription.Dispose();
            RegressionApp.Check(source.Removes == 1, "Repeated Dispose removed twice");
        }
        using (var source = new TestSource())
        {
            TimeSpan actual = default;
            var timing = new CanvasTimingInformation { UpdateCount = long.MaxValue - 2, TotalTime = TimeSpan.FromDays(4), ElapsedTime = TimeSpan.FromTicks(987654321987), IsRunningSlowly = true };
            var args = new CanvasAnimatedUpdateEventArgs(timing);
            using var subscription = Subscribe(source, null, delta => actual = delta);
            nint pointer = ((IWinRTObject)args).NativeObject.ThisPtr;
            RegressionApp.Check(source.Fire(pointer) == 0 && actual == timing.ElapsedTime, "Timing ABI layout/truncation mismatch");
            for (int i = 0; i < 1000; i++) source.Fire(pointer);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) source.Fire(pointer);
            RegressionApp.Check(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed Update callbacks allocated");
            RegressionApp.Check(source.Fire(0) == unchecked((int)0x80004003), "Null update args did not return E_POINTER");
            GC.KeepAlive(args);
        }
        using (var source = new TestSource { FailAdd = true })
        {
            try { Subscribe(source, () => { }); throw new Exception("Failed add was accepted"); }
            catch (COMException) { RegressionApp.Check(source.Removes == 0, "Failed add attempted to revoke an unregistered token"); }
        }
        using (var source = new TestSource())
        {
            using var subscription = Subscribe(source, () => throw new InvalidOperationException("boundary"));
            RegressionApp.Check(source.Fire(0) < 0, "Callback exception escaped or lost its HRESULT");
        }
        using (var source = new TestSource { FailRemove = true })
        {
            int calls = 0;
            var subscription = Subscribe(source, () => calls++);
            subscription.Dispose();
            RegressionApp.Check(source.Fire(1) == 0 && calls == 0, "Late callback accessed retired state after failed remove");
            RegressionApp.Check(source.HasHandler, "Fixture lost the handler despite remove failure");
            source.FailRemove = false;
            for (int i = 0; i < 64 && source.HasHandler; i++) source.Fire(1);
            RegressionApp.Check(!source.HasHandler, "Late callback did not reclaim the failed native registration");
        }
        using (var source = new TestSource())
        {
            FrameEventSubscription? subscription = null;
            subscription = Subscribe(source, () => subscription!.Dispose());
            RegressionApp.Check(source.Fire(0) == 0 && source.Removes == 1, "Self-unsubscription failed");
            subscription.Dispose();
        }
        using (var source = new TestSource())
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            FrameEventSubscription? subscription = null;
            subscription = Subscribe(source, () =>
            {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException();
                subscription!.Dispose(); // 与另一线程的 Dispose 并发，不能等待自己。
            });
            var callback = Task.Run(() => source.Fire(0));
            RegressionApp.Check(entered.Wait(5000), "Concurrent callback did not enter");
            var dispose = Task.Run(subscription.Dispose);
            RegressionApp.Check(source.Removed.Wait(5000), "Dispose did not remove before waiting");
            RegressionApp.Check(!dispose.IsCompleted, "Dispose returned while callback was active");
            release.Set();
            await Task.WhenAll(callback, dispose).WaitAsync(TimeSpan.FromSeconds(5));
            RegressionApp.Check(source.Removes == 1 && callback.Result == 0, "Concurrent/self Dispose failed");
        }
        using (var source = new TestSource { FailRemove = true })
        {
            var (subscription, target) = CreateCapturedTarget(source);
            subscription.Dispose();
            GC.Collect(); // 仅用于根引用回归，不作为生产缓解手段或性能样本。
            GC.WaitForPendingFinalizers();
            GC.Collect();
            RegressionApp.Check(!target.IsAlive, "Retired native handler retained its callback target");
            RegressionApp.Check(source.Fire(1) == 0, "Late native call failed after callback target collection");
            GC.KeepAlive(subscription);
        }
        using (var source = new TestSource())
        {
            var (owner, handler) = CreateAbandonedSubscription(source);
            CollectForOwnershipCheck();
            RegressionApp.Check(!owner.IsAlive && source.HasHandler, "Native handler rooted its abandoned subscription");
            RegressionApp.Check(source.Fire(1) == 0 && !source.HasHandler, "Orphan handler did not revoke on the next native callback");
            CollectForOwnershipCheck();
            RegressionApp.Check(!handler.IsAlive, "Revoked native handler was not collectible");
        }
    }

    internal static void CollectForOwnershipCheck()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Handler) CreateAbandonedSubscription(TestSource source)
    {
        var subscription = Subscribe(source, () => { });
        var handler = typeof(FrameEventSubscription).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(subscription)!;
        return (new(subscription), new(handler));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (FrameEventSubscription Subscription, WeakReference Target) CreateCapturedTarget(TestSource source)
    {
        var target = new object();
        return (Subscribe(source, () => GC.KeepAlive(target)), new WeakReference(target));
    }
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("32ec94c9-85ef-4342-af80-bd7bde7160ab")]
internal partial interface ITestSource
{
    [PreserveSig] int Reserved0();
    [PreserveSig] int Reserved1();
    [PreserveSig] int Reserved2();
    [PreserveSig] int Add(nint handler, out long token);
    [PreserveSig] int Remove(long token);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
internal partial interface ITestAgile { }

[GeneratedComClass]
internal sealed partial class TestSource : ITestSource, ITestAgile, IDisposable
{
    private readonly object _gate = new();
    private nint _handler;
    private nint _sender;
    internal bool FailAdd, FailRemove;
    internal int Removes;
    internal bool HasHandler { get { lock (_gate) return _handler != 0; } }
    internal ManualResetEventSlim Removed { get; } = new();
    public int Reserved0() => 0;
    public int Reserved1() => 0;
    public int Reserved2() => 0;
    public unsafe int Add(nint handler, out long token)
    {
        token = 42;
        if (FailAdd) return unchecked((int)0x80004005);
        if (_sender == 0) _sender = (nint)ComInterfaceMarshaller<ITestSource>.ConvertToUnmanaged(this);
        Marshal.AddRef(handler);
        _handler = handler;
        return 0;
    }
    public int Remove(long token)
    {
        Interlocked.Increment(ref Removes);
        Removed.Set();
        if (token != 42) return unchecked((int)0x80070057);
        if (FailRemove) return unchecked((int)0x80004005);
        lock (_gate)
        {
            if (_handler != 0) Marshal.Release(_handler);
            _handler = 0;
        }
        return 0;
    }
    internal unsafe int Fire(nint args)
    {
        nint handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler == 0) return 0;
            Marshal.AddRef(handler);
        }
        try { return ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)(*(nint**)handler)[3])(handler, _sender, args); }
        finally { Marshal.Release(handler); }
    }
    public unsafe void Dispose()
    {
        lock (_gate)
        {
            if (_handler != 0) Marshal.Release(_handler);
            _handler = 0;
        }
        if (_sender != 0) ComInterfaceMarshaller<ITestSource>.Free((void*)_sender);
        _sender = 0;
        Removed.Dispose();
    }
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("f34ea77c-5325-4b1b-80fa-b32319a0d267")]
internal partial interface ITestDrawArgs
{
    [PreserveSig] int Reserved0();
    [PreserveSig] int Reserved1();
    [PreserveSig] int Reserved2();
    [PreserveSig] int GetDrawingSession(out nint session);
}

/// <summary>Preserves native getter AddRef ownership while substituting only event args.</summary>
[GeneratedComClass]
internal sealed partial class TestDrawArgs : ITestDrawArgs, ITestAgile
{
    internal nint Session;
    internal int Reads;
    internal bool FailRead;
    public int Reserved0() => 0;
    public int Reserved1() => 0;
    public int Reserved2() => 0;
    public int GetDrawingSession(out nint session)
    {
        Reads++;
        session = FailRead ? 0 : Session;
        if (FailRead) return unchecked((int)0x80004005);
        Marshal.AddRef(session);
        return 0;
    }
}
