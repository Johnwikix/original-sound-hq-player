using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace WinUIMusicPlayer.State;

/// <summary>快捷键配置与冲突展示的唯一状态源；不持有原生注册或回调。</summary>
public sealed class HotKeyState : ObservableObject
{
    public List<string> PlayOrPauseShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "P"];
    public List<string> NextSongShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "Right"];
    public List<string> PreviousSongShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "Left"];
    public List<string> VolumeUpShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "Up"];
    public List<string> VolumeDownShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "Down"];
    public List<string> TogglePlayingDetailShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "Q"];
    public List<string> BackShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "B"];
    public List<string> ShowWindowShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "W"];
    public List<string> ToggleFullScreenShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "F"];
    public List<string> ToggleDesktopLyricsShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "D"];
    public List<string> ToggleDesktopLyricsLockShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "L"];
    public List<string> ToggleDesktopLyricsKaraokeShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "K"];
    public List<string> ResetDesktopLyricsShortcut { get; set => SetProperty(ref field, value); } = ["Ctrl", "Alt", "R"];
    public bool HasGlobalHotKeyConflict { get; internal set => SetProperty(ref field, value); }
    public string GlobalHotKeyConflictTitle { get; internal set => SetProperty(ref field, value); } = string.Empty;
}
