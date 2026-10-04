using Microsoft.UI.Dispatching;
using System;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>
/// 桌面歌词宿主边界。歌词窗口和渲染器不依赖具体宿主，后续壁纸模式只需增加实现。
/// </summary>
public interface IDesktopLyricsHost : IDisposable
{
    DesktopLyricsMode Mode { get; }

    void Attach(IntPtr hwnd, DispatcherQueue dispatcherQueue);

    void SetVisible(bool visible);

    void Refresh();
}

/// <summary>顶层悬浮宿主：窗口本身负责位置、可见性和交互。</summary>
internal sealed class FloatingDesktopLyricsHost : IDesktopLyricsHost
{
    public DesktopLyricsMode Mode => DesktopLyricsMode.Floating;

    public void Attach(IntPtr hwnd, DispatcherQueue dispatcherQueue)
    {
    }

    public void Refresh()
    {
    }

    public void SetVisible(bool visible)
    {
    }

    public void Dispose()
    {
    }
}
