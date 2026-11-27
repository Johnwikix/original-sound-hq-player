using CommunityToolkit.Mvvm.ComponentModel;
using System;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.State;

/// <summary>播放进度展示和交互状态；只在 UI 线程修改，不保存轮询快照或资源。</summary>
public sealed class PlaybackState : ObservableObject
{
    public PlaybackSelection? PendingSelection { get; internal set => SetProperty(ref field, value); }
    public Music? CurrentPlayingMusic
    {
        get => field;
        internal set
        {
            var previous = DisplayedPlaybackMusic;
            if (SetProperty(ref field, value) && !ReferenceEquals(previous, DisplayedPlaybackMusic))
                OnPropertyChanged(nameof(DisplayedPlaybackMusic));
        }
    }

    /// <summary>供播放页展示的曲目；曲间过渡可以早于实际队列提交。</summary>
    public Music? DisplayedPlaybackMusic => TransitionPresentation?.Music ?? CurrentPlayingMusic;

    /// <summary>过渡时钟的起点与身份。实时位置从播放进程快照读取。</summary>
    public PlaybackTransitionPresentation? TransitionPresentation
    {
        get => field;
        internal set
        {
            var previous = DisplayedPlaybackMusic;
            if (SetProperty(ref field, value) && !ReferenceEquals(previous, DisplayedPlaybackMusic))
                OnPropertyChanged(nameof(DisplayedPlaybackMusic));
        }
    }
    public double Volume { get; set => SetProperty(ref field, value); } = 50;
    public bool IsPlaying { get; internal set => SetProperty(ref field, value); }
    public bool CanSeekRemote { get; internal set => SetProperty(ref field, value); }
    public string PlayTimeText { get; set => SetProperty(ref field, value); } = "00:00/00:00";
    public string ElapsedTimeText { get; set => SetProperty(ref field, value); } = "00:00";
    public string RemainingTimeText { get; set => SetProperty(ref field, value); } = "00:00";
    public double ProgressSliderMax { get; set => SetProperty(ref field, value); } = 100;
    public TimeSpan CurrentPlayingTime { get; set => SetProperty(ref field, value); } = TimeSpan.Zero;
    public bool IsPlaybackEngineReady { get; set => SetProperty(ref field, value); }
    public bool IsUserDraggingProgressSlider { get; set => SetProperty(ref field, value); } = false;
    public bool IsManualSelect { get; set => SetProperty(ref field, value); } = false;
    public bool IsMouseOverProgressBar { get; set => SetProperty(ref field, value); } = false;
    public double ProgressSlider { get; set => SetProperty(ref field, value); } = 0;
}

/// <summary>立即呈现用户选曲意图，不提前改变播放统计、歌词或系统媒体信息。</summary>
public sealed record PlaybackSelection(Music Music, long EntryId);

/// <summary>表示手动预览、已开始的交叠，或等待状态管道同步的已提交过渡。</summary>
public readonly record struct PlaybackTransitionPresentation(Music Music, long EntryId, long Token = 0,
    long Epoch = 0, long PositionMs = 0, long DurationMs = 0, bool IsCommitted = false);
