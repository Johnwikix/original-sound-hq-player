#nullable enable
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WinRT;

namespace AnimatedWin2dControls.Interop;

/// <summary>Scopes one native input snapshot without creating PointerPoint/properties RCWs.</summary>
/// <remarks>Use on the event's UI thread, dispose once and do not copy this owning scope.</remarks>
public unsafe ref struct PointerPointScope
{
    private static readonly Guid ArgsIid = new("66e78a9a-1bec-5f92-b1a1-ea6334ee511c");
    private static readonly Guid ElementIid = new("c3c01020-320c-5cf6-9d24-d396bbfa4d8b");
    private nint _point;
    private PointerPointScope(nint point) => _point = point;

    /// <summary>Gets a snapshot in the same coordinate system as WinUI GetCurrentPoint.</summary>
    public static PointerPointScope GetCurrentPoint(PointerRoutedEventArgs args, UIElement? relativeTo)
    {
        ArgumentNullException.ThrowIfNull(args);
        ExceptionHelpers.ThrowExceptionForHR(((IWinRTObject)args).NativeObject.TryAs(ArgsIid, out nint source));
        ObjectReferenceValue element = default;
        try
        {
            element = MarshalInspectable<UIElement>.CreateMarshaler2(relativeTo!, ElementIid);
            nint point = 0;
            // IID/槽位来自本项目 WinUI 2.3.9 投影：GetCurrentPoint(11)、Position(10)。
            ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)source)[11])(
                source, element.GetAbi(), &point));
            if (point == 0) throw new InvalidOperationException("WinUI returned a null pointer point.");
            return new(point);
        }
        finally
        {
            element.Dispose();
            Marshal.Release(source);
            GC.KeepAlive(args);
            GC.KeepAlive(relativeTo);
        }
    }

    /// <summary>Gets the pointer position relative to the requested element.</summary>
    public readonly Point Position
    {
        get
        {
            ThrowIfDisposed();
            Point value = default;
            ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Point*, int>)(*(nint**)_point)[10])(_point, &value));
            return value;
        }
    }

    /// <summary>Gets MouseWheelDelta directly from the native properties snapshot.</summary>
    public readonly int MouseWheelDelta
    {
        get
        {
            ThrowIfDisposed();
            nint properties = 0;
            ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)_point)[11])(_point, &properties));
            if (properties == 0) throw new InvalidOperationException("WinUI returned null pointer properties.");
            try
            {
                int value = 0;
                ExceptionHelpers.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)properties)[19])(properties, &value));
                return value;
            }
            finally { Marshal.Release(properties); }
        }
    }

    private readonly void ThrowIfDisposed()
    {
        if (_point == 0) throw new ObjectDisposedException(nameof(PointerPointScope));
    }

    /// <summary>Releases the snapshot without retaining the event, element or page.</summary>
    public void Dispose()
    {
        nint point = _point;
        _point = 0;
        if (point != 0) Marshal.Release(point);
    }
}
