using System.Diagnostics;

namespace BassPlayerIpc.Shared;

/// <summary>One timestamp-based UI clock. Normal updates never rewind; a new seek/session epoch may.</summary>
public sealed class PlaybackTimeline
{
    private readonly object _gate = new();
    private ProgressSnapshot _snapshot;
    private long _displayMs;
    private bool _pendingPosition, _paused;
    private long _pendingSeekId;

    public bool Apply(ProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot.Revision <= _snapshot.Revision) return false;
            if (_pendingPosition && (_pendingSeekId > 0 ? snapshot.SeekId < _pendingSeekId : snapshot.Epoch == _snapshot.Epoch)) return false;
            bool discontinuity = _pendingPosition || snapshot.Epoch != _snapshot.Epoch;
            _snapshot = snapshot;
            _pendingPosition = false;
            _pendingSeekId = 0;
            if (discontinuity) _displayMs = Math.Max(0, snapshot.CurrentMs);
            return true;
        }
    }

    public (long curMs, long totalMs) Load() => ReadAt(Stopwatch.GetTimestamp());

    public (long curMs, long totalMs) ReadAt(long timestamp)
    {
        lock (_gate)
        {
            if (!_pendingPosition)
            {
                // Bounded extrapolation: stale/busy/stopped engine must not let lyrics run indefinitely.
                double elapsed = _snapshot.Playing && !_paused
                    ? Math.Clamp((timestamp - _snapshot.Timestamp) * 1000.0 / Stopwatch.Frequency, 0, 100) : 0;
                _displayMs = Math.Max(_displayMs, _snapshot.CurrentMs + (long)elapsed);
                if (_snapshot.TotalMs > 0) _displayMs = Math.Min(_displayMs, _snapshot.TotalMs);
            }
            return (_displayMs, _snapshot.TotalMs);
        }
    }

    /// <summary>Optimistic local seek/end. Ignore the previous epoch until the engine acknowledges a new timeline.</summary>
    public void Store(long currentMs, long totalMs)
    {
        lock (_gate)
        {
            _displayMs = Math.Max(0, currentMs);
            _snapshot = _snapshot with { TotalMs = totalMs };
            _pendingPosition = true;
            _pendingSeekId = 0;
        }
    }

    public void BeginSeek(long currentMs, long seekId)
    {
        lock (_gate)
        {
            Store(currentMs, _snapshot.TotalMs);
            _pendingSeekId = seekId;
        }
    }

    public void CancelSeek(long seekId)
    {
        lock (_gate)
        {
            if (!_pendingPosition || _pendingSeekId != seekId) return;
            _pendingPosition = false;
            _pendingSeekId = 0;
            _displayMs = Math.Max(0, _snapshot.CurrentMs);
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate) { ReadAt(Stopwatch.GetTimestamp()); _paused = paused; }
    }
}
