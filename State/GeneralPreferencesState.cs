using CommunityToolkit.Mvvm.ComponentModel;
using System;
using Windows.UI;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;

namespace WinUIMusicPlayer.State;

/// <summary>General 领域偏好的可观察状态；不执行 I/O 或启动后台任务。</summary>
public sealed class GeneralPreferencesState : ObservableObject
{
    public string DefaultEntryComboBoxTag { get; set => SetProperty(ref field, value); } = "AddFolder";
    public string DefaultPlayListComboBoxTag { get; set => SetProperty(ref field, value); } = "song";
    public bool IsFolderWatchEnabled { get; set => SetProperty(ref field, value); } = true;
    public string ArtistSplitSymbols { get; set => SetProperty(ref field, value); } = AppSettings.ArtistSplitSymbols;
    public bool IsAutoCoverEnabled { get; set => SetProperty(ref field, value); } = true;
    public bool IsRunningBackend { get; set => SetProperty(ref field, value); } = true;
    public string MusicCoverCache { get; set => SetProperty(ref field, value); }
    public bool EnableGlobalHotKey { get; set => SetProperty(ref field, value); } = false;
    public bool IsTrimOnHideEnabled { get; set => SetProperty(ref field, value); } = false;
    public bool IsTrimAfterPlaybackEnabled { get; set => SetProperty(ref field, value); } = false;
}
