using Microsoft.UI.Dispatching;
using System;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>桌面歌词宿主边界。歌词窗口和渲染器不依赖具体宿主。</summary>
public interface IDesktopLyricsHost : IDisposable
{
    DesktopLyricsMode Mode { get; }

    void Attach(IntPtr hwnd, DispatcherQueue dispatcherQueue);

    void SetVisible(bool visible);

    void Refresh();
}

/// <summary>宿主提供可选的客户区布局编辑能力；窗口不依赖具体宿主实现。</summary>
internal interface IDesktopLyricsBoundsHost
{
    bool TryGetBounds(out TaskbarWindowBounds bounds);

    void SetUserBounds(TaskbarWindowBounds bounds);

    void ResetUserBounds();

    /// <summary>更新任务栏歌词列宽；宿主负责把设置转换为窗口客户区边界。</summary>
    void SetLyricsWidth(int width);
}

internal readonly record struct TaskbarWindowBounds(int X, int Y, int Width, int Height);

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
