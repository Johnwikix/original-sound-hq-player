using System;
using System.Collections.Generic;
using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel.Controls;

/// <summary>以全局音频偏好为唯一状态源，编辑曲终自动过渡。</summary>
public sealed class PlaybackTransitionsViewModel : ObservableObject, IDisposable
{
    public PlaybackTransitionsViewModel()
    {
        AppSettings.PlaybackTransitionsChanged += OnTransitionsChanged;
    }

    public int ModeIndex
    {
        get
        {
            DspSettings settings = AppSettings.Dsp;
            if (!settings.GaplessPlayback) return 0;
            return settings.TransitionStyle switch
            {
                TrackTransitionStyle.Crossfade => 2,
                TrackTransitionStyle.Smart => 3,
                _ => 1
            };
        }
        set
        {
            if (value is < 0 or > 3 || value == ModeIndex) return;
            DspSettings saved = AppSettings.Dsp;
            AppSettings.Dsp = saved with
            {
                GaplessPlayback = value != 0,
                TransitionStyle = value switch
                {
                    1 => TrackTransitionStyle.Gapless,
                    2 => TrackTransitionStyle.Crossfade,
                    3 => TrackTransitionStyle.Smart,
                    _ => saved.TransitionStyle
                }
            };
        }
    }

    public bool CrossfadeVisible => ModeIndex is 2 or 3;
    public string CrossfadeDescription => ModeIndex == 3 ? ToolUtils.GetString("PlaybackTransitionSmartDescription") : "";

    public IReadOnlyList<string> DurationOptions { get; } = (string[])["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12"];

    public int DurationIndex
    {
        get => AppSettings.Dsp.CrossfadeSeconds - DspSettings.MinCrossfadeSeconds;
        set
        {
            // 初始化和隐藏时可能清除选择；这些通知不应改写已保存时长。
            if ((uint)value >= (uint)DurationOptions.Count || value == DurationIndex) return;
            AppSettings.Dsp = AppSettings.Dsp with { CrossfadeSeconds = value + DspSettings.MinCrossfadeSeconds };
        }
    }

    private void OnTransitionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ModeIndex));
        OnPropertyChanged(nameof(CrossfadeVisible));
        OnPropertyChanged(nameof(CrossfadeDescription));
        OnPropertyChanged(nameof(DurationIndex));
    }

    public void Dispose() => AppSettings.PlaybackTransitionsChanged -= OnTransitionsChanged;
}
