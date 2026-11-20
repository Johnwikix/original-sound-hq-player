using BassPlayerIpc.Shared;
using System;
using System.Diagnostics;
using WinUIMusicPlayer.State;

namespace WinUIMusicPlayer.Services;

/// <summary>Projects the selected occurrence on the UI and lyric render threads while the player finishes the outgoing track.</summary>
internal sealed class PlaybackPresentationClock
{
    private readonly object _gate = new();
    private PlaybackTransitionPresentation? _target;
    private ProgressSnapshot _snapshot;
    private long _position;
    private bool _paused;
    public PlaybackTimeline Confirmed { get; } = new();

    public void Apply(ProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            if (Confirmed.Apply(snapshot)) _snapshot = snapshot;
        }
    }

    public void SetTarget(PlaybackTransitionPresentation? target)
    {
        lock (_gate)
        {
            bool sameOccurrence = _target is { } previous && target is { } next
                && previous.EntryId == next.EntryId && previous.Token == next.Token && previous.Epoch == next.Epoch;
            long anchor = Math.Max(0, target?.PositionMs ?? 0);
            _position = sameOccurrence ? Math.Max(_position, anchor) : anchor;
            _target = target;
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            ReadAt(Stopwatch.GetTimestamp());
            _paused = paused;
            Confirmed.SetPaused(paused);
        }
    }

    public (long curMs, long totalMs) ReadAt(long timestamp)
    {
        lock (_gate)
        {
            if (_target is not { } target) return Confirmed.ReadAt(timestamp);
            if (target.Token != 0 && _snapshot.GaplessToken == target.Token && _snapshot.Epoch == target.Epoch)
                return Confirmed.ReadAt(timestamp);

            long duration = target.DurationMs;
            if (target.Token != 0 && _snapshot.TransitionToken == target.Token && _snapshot.TransitionEpoch == target.Epoch)
            {
                duration = _snapshot.TransitionTotalMs;
                double elapsed = _snapshot.Playing && !_paused
                    ? Math.Clamp((timestamp - _snapshot.Timestamp) * 1000.0 / Stopwatch.Frequency, 0, 100) : 0;
                _position = Math.Max(_position, _snapshot.TransitionCurrentMs + (long)(elapsed * _snapshot.TransitionPlaybackRate));
            }
            if (duration > 0) _position = Math.Min(_position, duration);
            return (_position, duration);
        }
    }
}
