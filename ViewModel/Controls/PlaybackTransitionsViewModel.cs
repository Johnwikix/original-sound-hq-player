using System;
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

    public double MinimumDuration => DspSettings.MinCrossfadeMilliseconds;
    public double MaximumDuration => DspSettings.MaxCrossfadeMilliseconds;
    public string DurationText => AppSettings.Dsp.CrossfadeMilliseconds.ToString();

    public double DurationMilliseconds
    {
        get => AppSettings.Dsp.CrossfadeMilliseconds;
        set
        {
            if (!double.IsFinite(value) || value < MinimumDuration || value > MaximumDuration) return;
            int durationMs = (int)Math.Round(value);
            if (durationMs == AppSettings.Dsp.CrossfadeMilliseconds) return;
            AppSettings.Dsp = AppSettings.Dsp with { CrossfadeMilliseconds = durationMs };
        }
    }

    private void OnTransitionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ModeIndex));
        OnPropertyChanged(nameof(CrossfadeVisible));
        OnPropertyChanged(nameof(CrossfadeDescription));
        OnPropertyChanged(nameof(DurationMilliseconds));
        OnPropertyChanged(nameof(DurationText));
    }

    public void Dispose() => AppSettings.PlaybackTransitionsChanged -= OnTransitionsChanged;
}
