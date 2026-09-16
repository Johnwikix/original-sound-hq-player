using System.Threading;
using WinUIMusicPlayer.WebService;

namespace WinUIMusicPlayer.Services;

/// <summary>冷却后只放行一个探测；用代次忽略冷却前请求的迟到结果。</summary>
internal sealed class LyricsSearchCircuitBreaker(int failureThreshold, long cooldownMs)
{
    private readonly Lock _sync = new();
    private int _failures;
    private int _generation;
    private long? _cooldownUntil;
    private bool _probeInFlight;

    public bool TryBegin(long now, out int generation)
    {
        lock (_sync)
        {
            generation = _generation;
            if (_cooldownUntil is { } until)
            {
                if (now < until || _probeInFlight) return false;
                _probeInFlight = true;
            }
            return true;
        }
    }

    // null 表示请求被取消/中断，不计为失败，也不阻塞下一次探测。
    public void Complete(int generation, LyricsSearchStatus? status, long now)
    {
        lock (_sync)
        {
            if (generation != _generation) return;
            _probeInFlight = false;
            if (status is null) return;
            if (status == LyricsSearchStatus.NetworkError)
            {
                if (++_failures >= failureThreshold)
                {
                    _cooldownUntil = now + cooldownMs;
                    _generation++;
                }
            }
            else
            {
                _failures = 0;
                if (_cooldownUntil is not null) _generation++;
                _cooldownUntil = null;
            }
        }
    }
}
