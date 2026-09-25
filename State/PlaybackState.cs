using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace WinUIMusicPlayer.State;

/// <summary>播放进度展示和交互状态；只在 UI 线程修改，不保存轮询快照或资源。</summary>
public sealed class PlaybackState : ObservableObject
{
    public PlaybackSelection? PendingSelection { get; internal set => SetProperty(ref field, value); }
    public WinUIMusicPlayer.Model.Music? CurrentPlayingMusic { get; internal set => SetProperty(ref field, value); }
    public double Volume { get; set => SetProperty(ref field, value); } = 50;
    public bool IsPlaying { get; internal set => SetProperty(ref field, value); }
    public string PlayTimeText { get; set => SetProperty(ref field, value); } = "00:00/00:00";
    public string ElapsedTimeText { get; set => SetProperty(ref field, value); } = "00:00";
    public string TotalTimeText { get; set => SetProperty(ref field, value); } = "00:00";
    public double BufferedProgress { get; set => SetProperty(ref field, value); }
    public bool HasRemoteBuffer { get; set => SetProperty(ref field, value); }
    public double ProgressSliderMax { get; set => SetProperty(ref field, value); } = 100;
    public TimeSpan CurrentPlayingTime { get; set => SetProperty(ref field, value); } = TimeSpan.Zero;
    public bool IsPlaybackEngineReady { get; set => SetProperty(ref field, value); }
    public bool IsUserDraggingProgressSlider { get; set => SetProperty(ref field, value); } = false;
    public bool IsManualSelect { get; set => SetProperty(ref field, value); } = false;
    public bool IsMouseOverProgressBar { get; set => SetProperty(ref field, value); } = false;
    public double ProgressSlider { get; set => SetProperty(ref field, value); } = 0;
}

/// <summary>立即呈现用户选曲意图，不提前改变播放统计、歌词或系统媒体信息。</summary>
public sealed record PlaybackSelection(WinUIMusicPlayer.Model.Music Music, long EntryId);
