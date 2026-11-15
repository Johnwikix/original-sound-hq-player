using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;

namespace WinUIMusicPlayer.Helper;

internal static class WindowActivationHelper
{
    internal static void Restore(AppWindow window)
    {
        window.Show(false);
        if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore(false);
    }

    internal static void RequestForeground(IntPtr window)
    {
        if (WindowHelper.SetForegroundWindow(window)) return;
        var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = 2, Count = 3 };
        FlashWindowEx(ref flash);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
