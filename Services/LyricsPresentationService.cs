using System;
using System.ComponentModel;
using AnimatedWin2dControls.Messages;
using Microsoft.UI.Dispatching;
using WinUIMusicPlayer.State;
namespace WinUIMusicPlayer.Services;

/// <summary>歌词渲染设置的广播与合并；不持有页面或兼容 ViewModel。</summary>
public sealed class LyricsPresentationService(AppState state) : IDisposable
{
    private bool _disposed, _started;
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        state.Presentation.PropertyChanged += PresentationChanged;
        LyricsSyncRequestBus.Requested += SendFullLyricsSync;
    }
    private void PresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed && e.PropertyName == nameof(state.Presentation.UILyrics)) UILyricsBus.Publish(state.Presentation.UILyrics);
    }
    private void SendFullLyricsSync()
    {
        if (_disposed) return;
        CancelPending();
        SendLyricsSettings();
        SendLyricsFontSize();
        IsPlayingBus.Publish(state.Playback.IsPlaying);
        TimeProgressBus.Publish((long)state.Playback.CurrentPlayingTime.TotalMilliseconds);
        OffsetMsBus.Publish(state.Playback.CurrentPlayingMusic?.LyricsOffsetMs ?? 0);
        if (state.Presentation.UILyrics.Count > 0) UILyricsBus.Publish(state.Presentation.UILyrics);
    }
    private DispatcherQueueTimer? _settingsDebounceTimer;

    public void ScheduleSettingsBroadcast()
    {
        if (_disposed || !state.Lifecycle.IsReady) return;
        if (_settingsDebounceTimer is null)
        {
            _settingsDebounceTimer = App.MainWindow.DispatcherQueue.CreateTimer();
            _settingsDebounceTimer.Interval = TimeSpan.FromMilliseconds(1000);
            _settingsDebounceTimer.IsRepeating = false;
            _settingsDebounceTimer.Tick += Tick;
        }
        _settingsDebounceTimer.Start();
    }

    private void Tick(DispatcherQueueTimer sender, object args) => SendLyricsSettings();
    public void SendLyricsSettings()
    {
        if (_disposed) return;

        string fontFamilyName = state.Preferences.FontFamily?.FontFamily?.Source ?? "Segoe UI";
        AnimatedWin2dControls.Messages.LyricsSettingsBus.Publish(new AnimatedWin2dControls.Messages.LyricsSettingsBus.Settings(
            fontFamilyName: fontFamilyName,
            lyricsTextAlignment: state.Preferences.LyricsAlignment,
            isDark: state.Preferences.IsDarkMode,
            scrollSensitivity: 1.0,
            // 固定字号模式不随竖屏放大；保留用户设置，只缩放渲染值。
            lyricsBlurAmount: state.Preferences.LyricsBlurAmount * (state.Preferences.IsPortraitLayout && !state.Preferences.IsGlobalFontSizeEnabled ? 1.6 : 1.0),
            glowAmount: state.Preferences.GlowAmount,
            charFloatAmount: state.Preferences.CharFloatAmount,
            charScaleAmount: state.Preferences.CharScaleAmount,
            longSyllableThreshold: state.Preferences.LongSyllableThreshold,
            isFadeOutEnabled: true,
            isOutOfSightEnabled: true,
            unplayedOpacity: state.Preferences.UnplayedOpacityPercent / 100.0,
            translatedOpacity: state.Preferences.TranslatedOpacityPercent / 100.0,
            strokeWidth: 0.0,
            scrollEasingType: state.Preferences.ScrollEasingType,
            scrollEasingMode: state.Preferences.ScrollEasingMode,
            playingLineTopOffset: state.Preferences.PlayingLineTopOffsetPercent / 100.0,
            targetFrameRate: state.Preferences.TargetFrameRate,
            isCustomColorEnabled: state.Preferences.IsCustomLyricsColorEnabled,
            lyricsCustomColor: state.Preferences.LyricsCustomColor,
            fontWeight: state.Preferences.LyricsFontWeight));
    }

    public void SendLyricsFontSize()
    {
        double fontSize = state.Preferences.IsGlobalFontSizeEnabled ? state.Preferences.GlobalFontSize : state.Preferences.LyricsFontSize;
        AnimatedWin2dControls.Messages.LyricsFontSizeBus.Publish(fontSize);
    }

    public void CancelPending() => _settingsDebounceTimer?.Stop();
    public void Dispose()
    {
        _disposed = true;
        state.Presentation.PropertyChanged -= PresentationChanged;
        LyricsSyncRequestBus.Requested -= SendFullLyricsSync;
        CancelPending();
        if (_settingsDebounceTimer is not null) _settingsDebounceTimer.Tick -= Tick;
    }
}
