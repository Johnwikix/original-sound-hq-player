using Microsoft.UI.Xaml;
using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;

public sealed partial class UnifiedLyricsCanvasControlV1
{
    private void UpdateTimerState()
    {
        var lyrics = _cachedUILyrics;
        bool shouldRun = _isPlaying && lyrics != null && lyrics.Count > 0;
        if (shouldRun)
        {
            if (_timer is null) CreateTimer();
            if (!_timer!.IsEnabled)
            {
                _lastTickAt = DateTimeOffset.UtcNow;
                _timer.Start();
            }
        }
        else
        {
            _timer?.Stop();
        }
    }

    private void CreateTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12.5) };
        _timer.Tick += OnTimerTick;
        _lastTickAt = DateTimeOffset.UtcNow;
    }

    private void DestroyTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer = null;
    }

    private void OnTimerTick(object? sender, object e)
    {
        var now = DateTimeOffset.UtcNow;
        var delta = now - _lastTickAt;
        _lastTickAt = now;
        if (delta > TimeSpan.FromSeconds(1)) delta = TimeSpan.FromMilliseconds(25);

        _currentTime += delta;
        MatchLyricLine(_currentTime);
        _canvas?.Invalidate();
    }
}
