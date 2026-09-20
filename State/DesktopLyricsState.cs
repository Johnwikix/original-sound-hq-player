using CommunityToolkit.Mvvm.ComponentModel;

namespace WinUIMusicPlayer.State;

/// <summary>桌面歌词公共偏好的唯一内存来源；所有写入和通知均在 UI 线程。</summary>
public sealed class DesktopLyricsState : ObservableObject
{
    public bool IsKaraokeEnabled { get; set => SetProperty(ref field, value); } = true;
    public bool IsEnabled { get; set => SetProperty(ref field, value); }
    public bool IsLocked { get; set => SetProperty(ref field, value); } = true;
    public bool AutoHideOnPlayingDetail { get; set => SetProperty(ref field, value); }
    public bool IsMainWindowShown { get; set => SetProperty(ref field, value); }
    public bool IsPlayingDetailVisible { get; set => SetProperty(ref field, value); }
}
