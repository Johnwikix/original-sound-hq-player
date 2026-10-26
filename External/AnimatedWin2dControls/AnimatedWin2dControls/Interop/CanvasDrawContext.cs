#nullable enable
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI;
using WinRT;

namespace AnimatedWin2dControls.Interop;

/// <summary>同步使用 Win2D 绘制会话 ABI；只允许在所属回调或会话作用域内使用。</summary>
/// <remarks>槽位与值布局来自 Win2D 1.4.0 原生头文件；不创建 DrawingSession/layer RCW。</remarks>
public readonly unsafe ref struct CanvasDrawContext
{
    private readonly nint _session;
    internal CanvasDrawContext(nint session) => _session = session;
    private nint Method(int slot) => (*(nint**)_session)[slot];

    // 未知的外部背景扩展仍可实现旧的托管 Draw 契约；内置渲染器不经过此回退。
    internal CanvasDrawingSession GetManagedSessionForCompatibility()
        => MarshalInspectable<CanvasDrawingSession>.FromAbi(_session);

    /// <summary>Gets or sets the current drawing transform.</summary>
    public Matrix3x2 Transform
    {
        get
        {
            Matrix3x2 value = default;
            ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Matrix3x2*, int>)Method(167))(_session, &value));
            return value;
        }
        set => ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Matrix3x2, int>)Method(168))(_session, value));
    }

    /// <summary>Gets the session DPI for diagnostics.</summary>
    public float Dpi
    {
        get
        {
            Guid iid = new("1a75b512-e9fa-49e6-a876-38cae194013e");
            ExceptionHelpers.ThrowExceptionForHR(Marshal.QueryInterface(_session, in iid, out nint source));
            try
            {
                float value = 0;
                ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, float*, int>)(*(nint**)source)[6])(source, &value));
                return value;
            }
            finally { Marshal.Release(source); }
        }
    }

    /// <summary>Clears the current render target.</summary>
    public void Clear(Color color)
        => ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Color, int>)Method(6))(_session, color));

    /// <summary>Submits pending native drawing commands.</summary>
    public void Flush()
        => ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Method(8))(_session));

    /// <summary>Draws a cached image at its origin.</summary>
    public void DrawImage(ICanvasImage image)
    {
        using var source = new CanvasInterfaceReference<ICanvasImage>(image);
        ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(9))(_session, source.GetAbi()));
        GC.KeepAlive(image);
    }

    /// <summary>Draws a cached image at an offset.</summary>
    public void DrawImage(ICanvasImage image, Vector2 offset)
    {
        using var source = new CanvasInterfaceReference<ICanvasImage>(image);
        ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Vector2, int>)Method(10))(_session, source.GetAbi(), offset));
        GC.KeepAlive(image);
    }

    /// <summary>Draws an image source rectangle into a destination rectangle.</summary>
    public void DrawImage(ICanvasImage image, Rect destination, Rect sourceRectangle)
    {
        using var source = new CanvasInterfaceReference<ICanvasImage>(image);
        ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, RectAbi, RectAbi, int>)Method(15))(
            _session, source.GetAbi(), new(destination), new(sourceRectangle)));
        GC.KeepAlive(image);
    }

    /// <summary>Draws an image rectangle with opacity.</summary>
    public void DrawImage(ICanvasImage image, Rect destination, Rect sourceRectangle, float opacity)
    {
        using var source = new CanvasInterfaceReference<ICanvasImage>(image);
        ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, RectAbi, RectAbi, float, int>)Method(18))(
            _session, source.GetAbi(), new(destination), new(sourceRectangle), opacity));
        GC.KeepAlive(image);
    }

    /// <summary>Fills a rectangle with a color.</summary>
    public void FillRectangle(Rect rectangle, Color color)
        => ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, RectAbi, Color, int>)Method(54))(_session, new(rectangle), color));

    /// <summary>Fills a rounded rectangle with a color.</summary>
    public void FillRoundedRectangle(Rect rectangle, float radiusX, float radiusY, Color color)
        => ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, RectAbi, float, float, Color, int>)Method(72))(_session, new(rectangle), radiusX, radiusY, color));

    /// <summary>Creates a scoped native opacity layer.</summary>
    public CanvasActiveLayerScope CreateLayer(float opacity)
    {
        nint layer = 0;
        ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, float, nint*, int>)Method(175))(_session, opacity, &layer));
        return new(layer);
    }

    /// <summary>Creates a scoped native opacity brush layer.</summary>
    public CanvasActiveLayerScope CreateLayer(ICanvasBrush opacityBrush)
    {
        using var brush = new CanvasInterfaceReference<ICanvasBrush>(opacityBrush);
        nint layer = 0;
        ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Method(176))(_session, brush.GetAbi(), &layer));
        GC.KeepAlive(opacityBrush);
        return new(layer);
    }

    /// <summary>Creates a scoped native opacity and clip layer.</summary>
    public CanvasActiveLayerScope CreateLayer(float opacity, Rect rectangle)
    {
        nint layer = 0;
        ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, float, RectAbi, nint*, int>)Method(177))(_session, opacity, new(rectangle), &layer));
        return new(layer);
    }

    // WinRT Rect 的 ABI 是四个 float；这里从公开的 double 属性显式转换。
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RectAbi(Rect value)
    {
        private readonly float _x = (float)value.X;
        private readonly float _y = (float)value.Y;
        private readonly float _width = (float)value.Width;
        private readonly float _height = (float)value.Height;
    }
}

/// <summary>Owns one native layer until Close and Release, without a managed WinRT wrapper.</summary>
/// <remarks>Do not copy the owning scope; use it synchronously in one using statement.</remarks>
public ref struct CanvasActiveLayerScope
{
    private nint _layer;
    internal CanvasActiveLayerScope(nint layer) => _layer = layer;

    /// <summary>Closes the layer and releases its COM reference once.</summary>
    public void Dispose()
    {
        nint layer = _layer;
        _layer = 0;
        if (layer != 0) CanvasDrawingSessionScope.CloseAndRelease(layer);
    }
}

/// <summary>Owns a callback reference or an explicitly created native drawing session.</summary>
/// <remarks>Do not copy the owning scope or retain its context after disposal.</remarks>
public unsafe ref struct CanvasDrawingSessionScope
{
    private static readonly Guid DrawingSessionIid = new("f60afd09-e623-4be0-b750-578aa920b1db");
    private static readonly Guid RenderTargetIid = new("2d4c7349-9a32-41b9-b3cc-caf1b7e1099b");
    private static readonly Guid ClosableIid = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    private nint _session;
    private readonly bool _close;
    private object? _owner;

    private CanvasDrawingSessionScope(nint session, bool close, object? owner = null)
    {
        _session = session;
        _close = close;
        _owner = owner;
    }

    /// <summary>Gets the context valid until this scope is disposed.</summary>
    public readonly CanvasDrawContext Context
        => _session != 0 ? new(_session) : throw new ObjectDisposedException(nameof(CanvasDrawingSessionScope));

    /// <summary>Retains a managed session for compatibility with existing public rendering APIs.</summary>
    public static CanvasDrawingSessionScope Borrow(CanvasDrawingSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ExceptionHelpers.ThrowExceptionForHR(((IWinRTObject)session).NativeObject.TryAs(DrawingSessionIid, out nint pointer));
        return new(pointer, false, session);
    }

    /// <summary>Creates an owned native session on a cached render target.</summary>
    public static CanvasDrawingSessionScope Create(CanvasRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ExceptionHelpers.ThrowExceptionForHR(((IWinRTObject)target).NativeObject.TryAs(RenderTargetIid, out nint pointer));
        try
        {
            nint session = 0;
            ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)pointer)[6])(pointer, &session));
            return new(session, true, target);
        }
        finally { Marshal.Release(pointer); GC.KeepAlive(target); }
    }

    /// <summary>Closes owned intermediate sessions; callback sessions are only released.</summary>
    public void Dispose()
    {
        nint session = _session;
        _session = 0;
        try
        {
            if (session != 0)
            {
                if (_close) CloseAndRelease(session);
                else Marshal.Release(session);
            }
        }
        finally { GC.KeepAlive(_owner); _owner = null; }
    }

    internal static void CloseAndRelease(nint pointer)
    {
        try
        {
            ExceptionHelpers.ThrowExceptionForHR(Marshal.QueryInterface(pointer, in ClosableIid, out nint closable));
            try { ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)closable)[6])(closable)); }
            finally { Marshal.Release(closable); }
        }
        finally { Marshal.Release(pointer); }
    }
}

/// <summary>Scopes CsWinRT's value marshaler without boxing it as IDisposable.</summary>
internal ref struct CanvasInterfaceReference<T> where T : class
{
    private ObjectReferenceValue _reference;
    internal CanvasInterfaceReference(T value) => _reference = MarshalInterface<T>.CreateMarshaler2(value);
    internal readonly nint GetAbi() => _reference.GetAbi();
    public void Dispose()
    {
        var reference = _reference;
        _reference = default;
        reference.Dispose();
    }
}
