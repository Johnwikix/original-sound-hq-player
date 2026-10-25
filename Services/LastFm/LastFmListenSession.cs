using BassPlayerIpc.Shared;
using System;
using System.Diagnostics;

namespace WinUIMusicPlayer.Services.LastFm;

/// <summary>One confirmed playback occurrence, independent of the local statistics policy.</summary>
public sealed class LastFmListenSession(LastFmTrack track, ProgressSnapshot initial, long startedAt)
{
    private ProgressSnapshot _previous = initial;
    public Guid Id { get; } = Guid.NewGuid();
    public LastFmTrack Track { get; private set; } = track;
    public long StartedAt { get; } = startedAt;
    public double ListenedMilliseconds { get; private set; }
    public bool Enqueued { get; set; }
    public bool EnqueuePending { get; set; }
    public bool NowPlayingSent { get; set; }
    public bool IsQualified => Track.DurationSeconds > 30
        && ListenedMilliseconds >= Math.Min(Track.DurationSeconds * 500.0, 240000);

    public void Rebase(ProgressSnapshot snapshot) => _previous = snapshot;

    public void Observe(ProgressSnapshot current)
    {
        var previous = _previous;
        _previous = current;
        if (current.TotalMs > 0)
        {
            int seconds = (int)Math.Clamp(Math.Ceiling(current.TotalMs / 1000.0), 0, int.MaxValue);
            if (seconds != Track.DurationSeconds) Track = Track with { DurationSeconds = seconds };
        }
        if (!previous.Playing || !current.Playing || current.Epoch != previous.Epoch
            || current.SeekId != previous.SeekId || current.GaplessToken != previous.GaplessToken) return;
        double elapsed = (current.Timestamp - previous.Timestamp) * 1000.0 / Stopwatch.Frequency;
        long advanced = current.CurrentMs - previous.CurrentMs;
        double rate = double.IsFinite(current.PlaybackRate) ? Math.Clamp(current.PlaybackRate, 0.25, 5) : 1;
        // Count fresh, continuous rendered progress only. Seeking never grants listening time;
        // wall time caps the credit even when a decoder reports a large forward discontinuity.
        if (elapsed <= 0 || elapsed > 2000 || advanced <= 0 || advanced > elapsed * rate + 250) return;
        ListenedMilliseconds += Math.Min(elapsed, advanced / rate);
    }
}
