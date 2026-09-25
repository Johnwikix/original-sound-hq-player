using AnimatedWin2dControls.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.State;

namespace WinUIMusicPlayer.Services;

/// <summary>拥有播放时钟、轮询与 UI 发布定时器；退出等待所有轮询代次后才释放 IPC。</summary>
public sealed class PlaybackProgressService(AppState state, SystemMediaControlsService media, ILogger<PlaybackProgressService> logger)
{
    private readonly BassPlayerIpc.Shared.PlaybackTimeline _cache = new();
    private DispatcherQueue? _dispatcher;
    private IpcService? _source;
    private DispatcherQueueTimer? _progressTimer;
    private CancellationTokenSource? _progressPollingCts;
    private Task? _progressPollingTask;
    private bool _isDisposed;
    private int _lastDisplayedSecond = -1;
    private int _lastDisplayedTotalSecond = -1;
    private long _lastSmtcUpdateTick;
    private TimeSpan CurrentTime;
    private TimeSpan TotalTime;
    public event Action<long>? CurrentPlayingTimeChanged;

    /// <summary>启动时显式接入已存在的 IPC 与 UI 调度器，构造函数不启动后台工作。</summary>
    public void Attach(IpcService source, DispatcherQueue dispatcher)
    {
        if (_isDisposed) return;
        _source = source;
        _dispatcher = dispatcher;
    }

    private void Dispatch(DispatcherQueueHandler action)
    {
        if (_isDisposed || _dispatcher is null) return;
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(action);
    }
    private void UpdateCore()
    {
        if (_source?.TryGetProgressSnapshot(out var snapshot) == true) _cache.Apply(snapshot);
        OnProgressTick(null, null!);
    }

    public async Task StopAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            TimeProgressBus.SetClock(null);
            StopProgressTimerCore();
            if (_progressTimer is not null) _progressTimer.Tick -= OnProgressTick;
        }
        if (_progressPollingTask is not null) await _progressPollingTask;
        _source = null;
    }
    public void StartProgressTimer()
    {
        Dispatch(StartProgressTimerCore);
    }

    private void StartProgressTimerCore()
    {
        if (_isDisposed || !state.Lifecycle.IsReady || !state.Playback.IsPlaybackEngineReady) return;
        TimeProgressBus.SetClock(ReadPlaybackClock);
        _cache.SetPaused(false);
        if (_progressTimer is null)
        {
            _progressTimer = _dispatcher!.CreateTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(125);
            _progressTimer.IsRepeating = true;
            _progressTimer.Tick += OnProgressTick;
        }
        bool newPollingGeneration = _progressPollingCts is null || _progressPollingCts.IsCancellationRequested || _progressPollingTask?.IsCompleted == true;
        if (newPollingGeneration)
        {
            _progressPollingCts = new CancellationTokenSource();
        }
        if (newPollingGeneration || _progressPollingTask is null || _progressPollingTask.IsCompleted)
        {
            var previous = _progressPollingTask;
            var owner = _progressPollingCts!;
            _progressPollingTask = RunPollingGenerationAsync(previous, owner);
        }
        _progressTimer.Start();
    }

    private async Task RunPollingGenerationAsync(Task? previous, CancellationTokenSource owner)
    {
        try
        {
            if (previous is not null) await previous;
            await Task.Run(() => PollProgressLoopAsync(owner.Token));
        }
        finally { owner.Dispose(); }
    }

    public void StopProgressTimer()
    {
        Dispatch(StopProgressTimerCore);
    }

    private void StopProgressTimerCore()
    {
        _cache.SetPaused(true);
        _progressTimer?.Stop();
        if (_progressPollingTask is { IsCompleted: false }) _progressPollingCts?.Cancel();
    }

    public void UpdateProgressTimerUI()
    {
        Dispatch(UpdateCore);
    }

    public (long curMs, long totalMs) GetTimeProgressCache() => _cache.Load();
    public void BeginProgressSeek(long positionMs, long seekId) => _cache.BeginSeek(positionMs, seekId);
    public void CancelProgressSeek(long seekId) => _cache.CancelSeek(seekId);
    private long ReadPlaybackClock() => _cache.Load().curMs;

    public void MarkPlaybackEnded(long totalMs) => _cache.MarkPlaybackEnded(totalMs);

    private async Task PollProgressLoopAsync(CancellationToken ct)
    {
        var svc = _source;
        if (svc is null) return;
        try
        {
            using var pt = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (await pt.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (svc.TryGetProgressSnapshot(out var snapshot) && !ct.IsCancellationRequested)
                        _cache.Apply(snapshot);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "进度轮询失败");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void OnProgressTick(DispatcherQueueTimer? sender, object args)
    {
        if (_isDisposed || state.Playback.IsUserDraggingProgressSlider) return;
        try
        {
            var (curMs, totalMs) = _cache.Load();
            TotalTime = TimeSpan.FromMilliseconds(totalMs);
            CurrentTime = TimeSpan.FromMilliseconds(curMs);
            state.Playback.CurrentPlayingTime = CurrentTime;
            CurrentPlayingTimeChanged?.Invoke(curMs);
            TimeProgressBus.Publish(curMs);

            if (state.Playback.IsManualSelect) return;

            state.Playback.ProgressSlider = curMs / 1000.0;
            state.Playback.ProgressSliderMax = totalMs / 1000.0;

            int currentSecond = (int)CurrentTime.TotalSeconds;
            int totalSecond = (int)TotalTime.TotalSeconds;
            if (currentSecond == _lastDisplayedSecond && totalSecond == _lastDisplayedTotalSecond) return;
            _lastDisplayedSecond = currentSecond;
            _lastDisplayedTotalSecond = totalSecond;

            state.Playback.PlayTimeText = CurrentTime.Hours >= 1
                ? string.Create(17, (curMs, totalMs), WriteTimeWithHours)
                : string.Create(11, (curMs, totalMs), WriteTimeNoHours);
            state.Playback.ElapsedTimeText = CurrentTime.TotalHours >= 1
                ? CurrentTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : CurrentTime.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            // 滑块右侧显示剩余时间（总时长 − 当前进度）；curMs 可能瞬时越过 totalMs，钳到 0 避免负值。
            long remainingMs = totalMs - curMs;
            if (remainingMs < 0) remainingMs = 0;
            var remaining = TimeSpan.FromMilliseconds(remainingMs);
            state.Playback.RemainingTimeText = remaining.TotalHours >= 1
                ? remaining.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : remaining.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            if (Environment.TickCount64 - _lastSmtcUpdateTick >= 250)
            {
                _lastSmtcUpdateTick = Environment.TickCount64;
                media.UpdateTimelineProperties(CurrentTime, TotalTime);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpdateProgressTimerUI 更新进度条UI失败");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTimeWithHours(Span<char> d, (long curMs, long totMs) s)
    {
        var cur = TimeSpan.FromMilliseconds(s.curMs);
        var tot = TimeSpan.FromMilliseconds(s.totMs);
        cur.Hours.TryFormat(d.Slice(0, 2),   out _, "D2", CultureInfo.InvariantCulture);
        d[2] = ':';
        cur.Minutes.TryFormat(d.Slice(3, 2), out _, "D2", CultureInfo.InvariantCulture);
        d[5] = ':';
        cur.Seconds.TryFormat(d.Slice(6, 2), out _, "D2", CultureInfo.InvariantCulture);
        d[8] = '/';
        tot.Hours.TryFormat(d.Slice(9, 2),   out _, "D2", CultureInfo.InvariantCulture);
        d[11] = ':';
        tot.Minutes.TryFormat(d.Slice(12, 2), out _, "D2", CultureInfo.InvariantCulture);
        d[14] = ':';
        tot.Seconds.TryFormat(d.Slice(15, 2), out _, "D2", CultureInfo.InvariantCulture);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTimeNoHours(Span<char> d, (long curMs, long totMs) s)
    {
        var cur = TimeSpan.FromMilliseconds(s.curMs);
        var tot = TimeSpan.FromMilliseconds(s.totMs);
        cur.Minutes.TryFormat(d.Slice(0, 2), out _, "D2", CultureInfo.InvariantCulture);
        d[2] = ':';
        cur.Seconds.TryFormat(d.Slice(3, 2), out _, "D2", CultureInfo.InvariantCulture);
        d[5] = '/';
        tot.Minutes.TryFormat(d.Slice(6, 2), out _, "D2", CultureInfo.InvariantCulture);
        d[8] = ':';
        tot.Seconds.TryFormat(d.Slice(9, 2), out _, "D2", CultureInfo.InvariantCulture);
    }

}
