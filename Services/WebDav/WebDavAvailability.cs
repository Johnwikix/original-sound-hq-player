using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>来源连通性与重试期限；不决定单曲（可能已有完整缓存）能否播放。</summary>
public sealed class WebDavAvailability(TimeProvider? clock = null)
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<int, Status> _sources = [];
    private sealed record Status(long Version, bool Offline, long FailedAt);

    public long Version(int sourceId) { lock (_gate) return _sources.GetValueOrDefault(sourceId)?.Version ?? 0; }
    public bool IsOffline(int sourceId) { lock (_gate) return _sources.GetValueOrDefault(sourceId)?.Offline == true; }
    public bool ShouldDefer(int sourceId)
    {
        lock (_gate)
            return _sources.TryGetValue(sourceId, out var status) && status.Offline &&
                _clock.GetElapsedTime(status.FailedAt) < RetryDelay;
    }
    public void ReportFailure(int sourceId)
    {
        lock (_gate)
            _sources[sourceId] = new(Version(sourceId) + 1, true, _clock.GetTimestamp());
    }
    public bool CompleteProbe(int sourceId, long version, bool offline)
    {
        lock (_gate)
        {
            // 断流发生前启动的探活，不得在迟到时把来源改回在线。
            if (Version(sourceId) != version) return false;
            bool changed = !_sources.TryGetValue(sourceId, out var previous) || previous.Offline != offline;
            _sources[sourceId] = new(version + 1, offline, offline ? _clock.GetTimestamp() : 0);
            return changed;
        }
    }
}
