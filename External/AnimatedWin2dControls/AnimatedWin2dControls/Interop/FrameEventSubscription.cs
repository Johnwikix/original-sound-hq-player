#nullable enable
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using WinRT;

namespace AnimatedWin2dControls.Interop;

/// <summary>在当前 Draw 回调作用域内同步消费原始绘制上下文。</summary>
public delegate void CanvasDrawHandler(CanvasDrawContext context);

/// <summary>订阅高频帧事件，回调参数保持原始 ABI 指针，不创建逐帧 CsWinRT 包装。</summary>
/// <remarks>
/// 所有者必须显式释放订阅。Dispose 禁止新工作并等待在途回调；从回调内部释放时，
/// 引用延迟到该回调返回才释放。注册/释放须在事件源允许的线程执行。
/// </remarks>
public sealed unsafe class FrameEventSubscription : IDisposable
{
    // IID/槽位来自 Microsoft.WinUI 2.3.9 与 Win2D 1.4.0 的元数据/原生头文件。
    private static readonly Guid CompositionTargetStatics = new("12a4be6f-6db1-5165-b622-d57ab782745b");
    private static readonly Guid CanvasAnimatedControlInterface = new("9bd47d0d-d57d-43b7-82cb-489cc566e887");
    private readonly object _callbackGate = new();
    private IObjectReference? _source;
    private Action? _rendering;
    private Action<TimeSpan>? _update;
    private CanvasDrawHandler? _draw;
    private readonly int _removeSlot;
    private long _token;
    private int _stopping;
    private int _revokeStarted;
    private int _callbackDepth;
    private bool _detached;
    private bool _registered;

    private FrameEventSubscription(IObjectReference source, int removeSlot, Action? rendering, Action<TimeSpan>? update)
    {
        _source = source;
        _removeSlot = removeSlot;
        _rendering = rendering;
        _update = update;
    }

    /// <summary>在当前 XAML UI 线程订阅 Rendering，忽略原生 sender/args。</summary>
    public static FrameEventSubscription SubscribeRendering(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        // 仅在冷路径使用运行时工厂解析，保留 unpackaged/reg-free WinUI 的激活回退。
        var source = ActivationFactory.Get("Microsoft.UI.Xaml.Media.CompositionTarget", CompositionTargetStatics);
        var subscription = new FrameEventSubscription(source, 7, callback, null);
        try
        {
            var handler = new RawRenderingHandler(subscription);
            void* pointer = ComInterfaceMarshaller<IRawRenderingHandler>.ConvertToUnmanaged(handler);
            try { subscription.Register(6, (nint)pointer); }
            finally { ComInterfaceMarshaller<IRawRenderingHandler>.Free(pointer); }
            return subscription;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    /// <summary>订阅 Win2D Update，只把原生 timing 中的 elapsed time 交给现有渲染逻辑。</summary>
    public static FrameEventSubscription SubscribeUpdate(CanvasAnimatedControl canvas, Action<TimeSpan> callback)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(callback);
        var native = ((IWinRTObject)canvas).NativeObject;
        Guid iid = CanvasAnimatedControlInterface;
        Marshal.ThrowExceptionForHR(native.TryAs(iid, out nint source));
        try
        {
            var subscription = new FrameEventSubscription(OwnSource(source, iid), 9, null, callback);
            try
            {
                var handler = new RawCanvasUpdateHandler(subscription);
                void* pointer = ComInterfaceMarshaller<IRawCanvasUpdateHandler>.ConvertToUnmanaged(handler);
                try { subscription.Register(8, (nint)pointer); }
                finally { ComInterfaceMarshaller<IRawCanvasUpdateHandler>.Free(pointer); }
                return subscription;
            }
            catch
            {
                subscription.Dispose();
                throw;
            }
        }
        finally
        {
            Marshal.Release(source);
            GC.KeepAlive(canvas);
        }
    }

    // 只在订阅冷路径保留一个有 apartment 管理的源引用；参数回调不走 FromAbi/As。
    private static IObjectReference OwnSource(nint source, Guid iid)
        => ComWrappersSupport.GetObjectReferenceForInterface(source, iid, requireQI: false);

    /// <summary>订阅 Win2D Draw，事件参数和主绘制会话均不创建 RCW。</summary>
    public static FrameEventSubscription SubscribeDraw(CanvasAnimatedControl canvas, CanvasDrawHandler callback)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(callback);
        var native = ((IWinRTObject)canvas).NativeObject;
        Guid iid = CanvasAnimatedControlInterface;
        Marshal.ThrowExceptionForHR(native.TryAs(iid, out nint source));
        try
        {
            var subscription = new FrameEventSubscription(OwnSource(source, iid), 11, null, null) { _draw = callback };
            try
            {
                var handler = new RawCanvasDrawHandler(subscription);
                void* pointer = ComInterfaceMarshaller<IRawCanvasDrawHandler>.ConvertToUnmanaged(handler);
                try { subscription.Register(10, (nint)pointer); }
                finally { ComInterfaceMarshaller<IRawCanvasDrawHandler>.Free(pointer); }
                return subscription;
            }
            catch { subscription.Dispose(); throw; }
        }
        finally { Marshal.Release(source); GC.KeepAlive(canvas); }
    }

    private void Register(int addSlot, nint handler)
    {
        // 注册期间也可能已有游戏循环；先发布 token/注册状态，再允许其他线程进入回调。
        lock (_callbackGate)
        {
            try
            {
                nint source = _source!.ThisPtr;
                long token = 0;
                int hr = ((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)(*(nint**)source)[addSlot])(source, handler, &token);
                GC.KeepAlive(_source);
                Marshal.ThrowExceptionForHR(hr);
                _token = token;
                _registered = true;
            }
            catch
            {
                _source?.Dispose();
                _source = null;
                _rendering = null;
                _update = null;
                _draw = null;
                throw;
            }
        }
    }

    internal int Invoke(nint args)
    {
        lock (_callbackGate)
        {
            if (Volatile.Read(ref _stopping) != 0) return 0;
            _callbackDepth++;
            try
            {
                if (_draw is { } draw)
                {
                    if (args == 0) return unchecked((int)0x80004003);
                    nint session = 0;
                    int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)args)[6])(args, &session);
                    if (hr < 0) return hr;
                    if (session == 0) return unchecked((int)0x80004003);
                    try { draw(new CanvasDrawContext(session)); }
                    // 主交换链会话由 Win2D 在回调返回后 EndDraw；这里只释放 getter 的引用。
                    finally { Marshal.Release(session); }
                }
                else if (_update is { } update)
                {
                    // Win2D 的 TypedEventHandler 参数即默认接口指针。三个 INT64 + 一个 ABI boolean；
                    // .NET 11 热路径只读该结构，不创建事件参数/时间信息 RCW。
                    if (args == 0) return unchecked((int)0x80004003);
                    CanvasTimingAbi timing = default;
                    int hr = ((delegate* unmanaged[Stdcall]<nint, CanvasTimingAbi*, int>)(*(nint**)args)[6])(args, &timing);
                    if (hr < 0) return hr;
                    update(TimeSpan.FromTicks(timing.ElapsedTime));
                }
                else
                {
                    _rendering?.Invoke();
                }
                return 0;
            }
            catch (Exception error)
            {
                // 保留 WinRT 的 HRESULT/错误信息语义，异常不得穿过反向 ABI 边界。
                ExceptionHelpers.SetErrorInfo(error);
                return ExceptionHelpers.GetHRForException(error);
            }
            finally
            {
                _callbackDepth--;
                if (_detached && _callbackDepth == 0) ReleaseReferences();
            }
        }
    }

    /// <summary>解绑事件并排空已进入的回调；重复释放安全。</summary>
    public void Dispose()
    {
        Volatile.Write(ref _stopping, 1);
        if (Interlocked.CompareExchange(ref _revokeStarted, 1, 0) == 0)
        {
            try
            {
                // 不持回调锁执行原生 remove，避免与原生事件源自己的锁相互等待。
                if (_registered && _source is { } reference)
                {
                    nint source = reference.ThisPtr;
                    int hr = ((delegate* unmanaged[Stdcall]<nint, long, int>)(*(nint**)source)[_removeSlot])(source, _token);
                    GC.KeepAlive(reference);
                    if (hr < 0)
                        Trace.TraceError("Frame event unsubscription failed: 0x{0:X8}", hr);
                }
            }
            catch (Exception error)
            {
                // 源已关闭/设备退出时 remove 也可能失败；仍须排空回调并释放其余资源。
                Trace.TraceError("Frame event unsubscription failed: {0}", error);
            }
            finally
            {
                lock (_callbackGate)
                {
                    _detached = true;
                    _registered = false;
                    try
                    {
                        if (_callbackDepth == 0) ReleaseReferences();
                    }
                    finally { Monitor.PulseAll(_callbackGate); }
                }
            }
        }
        else if (!Monitor.IsEntered(_callbackGate))
        {
            lock (_callbackGate)
            {
                while (!_detached) Monitor.Wait(_callbackGate);
            }
        }
    }

    private void ReleaseReferences()
    {
        _rendering = null;
        _update = null;
        _draw = null;
        var source = _source;
        _source = null;
        source?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CanvasTimingAbi
    {
        public long UpdateCount;
        public long TotalTime;
        public long ElapsedTime;
        public byte IsRunningSlowly;
    }
}

// .NET 生成 IUnknown/引用计数/Invoke 桥；参数明确为 nint，避免 CsWinRT 的事件参数投影。
[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("c50898f6-c536-5f47-8583-8b2c2438a13b")]
internal partial interface IRawRenderingHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("f3505553-11e9-548c-80b6-33e56d2e673b")]
internal partial interface IRawCanvasUpdateHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("00ddd999-594e-5c0f-b4a0-52b01171dcd6")]
internal partial interface IRawCanvasDrawHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
internal partial interface IRawAgileObject { }

/// <summary>原生事件源只持有弱所有者，遗漏释放也不会把宿主及其资源永久根住。</summary>
[GeneratedComClass]
internal sealed partial class RawRenderingHandler(FrameEventSubscription owner) : IRawRenderingHandler, IRawAgileObject
{
    private readonly WeakReference<FrameEventSubscription> _owner = new(owner);
    public int Invoke(nint sender, nint args) => _owner.TryGetTarget(out var owner) ? owner.Invoke(args) : 0;
}

/// <summary>允许 Win2D 游戏循环直接调用的原始参数委托。</summary>
[GeneratedComClass]
internal sealed partial class RawCanvasUpdateHandler(FrameEventSubscription owner) : IRawCanvasUpdateHandler, IRawAgileObject
{
    private readonly WeakReference<FrameEventSubscription> _owner = new(owner);
    public int Invoke(nint sender, nint args) => _owner.TryGetTarget(out var owner) ? owner.Invoke(args) : 0;
}

/// <summary>原生 Draw 委托不强根住订阅所有者。</summary>
[GeneratedComClass]
internal sealed partial class RawCanvasDrawHandler(FrameEventSubscription owner) : IRawCanvasDrawHandler, IRawAgileObject
{
    private readonly WeakReference<FrameEventSubscription> _owner = new(owner);
    public int Invoke(nint sender, nint args) => _owner.TryGetTarget(out var owner) ? owner.Invoke(args) : 0;
}
