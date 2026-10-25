using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

public sealed record LastFmHistoryEntry(int Id, string Title, string Artist, string Album, double TotalDurationMs,
    double DurationPlayedMs, DateTime StartedAt, DateTime EndedAt);
public sealed record LastFmKnownPlay(string ApiKey, string Account, string TrackKey, long StartedAt, bool Retimed = false, bool Uncertain = false);
public sealed record LastFmRecentPlay(string Title, string Artist, long StartedAt);
public sealed record LastFmRecentPage(IReadOnlyList<LastFmRecentPlay> Plays, int TotalPages);
public sealed record LastFmHistoryImportResult(int Queued, int AlreadyPresent, int TooOld, int Ineligible, int Deferred, int Retimed = 0, int Uncertain = 0);
internal sealed record LastFmHistoryCandidate(LastFmTrack Track, long StartedAt, string Key, bool Retimed);
internal sealed record LastFmHistoryBatch(List<LastFmHistoryCandidate> Candidates, int AlreadyPresent, int TooOld, int Ineligible, int Deferred);

/// <summary>Read-only, bounded pages from the existing statistics database. The database retains ownership.</summary>
public interface ILastFmHistorySource
{
    Task<int> CountOlderAsync(DateTime fromUtc, CancellationToken token);
    IAsyncEnumerable<LastFmHistoryEntry> ReadAsync(DateTime fromUtc, DateTime toUtc, CancellationToken token);
}

internal static class LastFmHistory
{
    internal const int LookbackDays = 14;
    internal const int ReceiptLimit = 500000;
    internal static long OldestTimestamp => DateTimeOffset.UtcNow.AddDays(-LookbackDays).ToUnixTimeSeconds();

    internal static string TrackKey(string title, string artist)
    {
        string normalized = title.Trim().Normalize().ToUpperInvariant() + "\0" + artist.Trim().Normalize().ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    internal static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static string SourceKey(LastFmHistoryEntry entry)
    {
        // The source row and original UTC ticks identify the occurrence, including after metadata edits.
        string key = entry.Id.ToString(CultureInfo.InvariantCulture) + ":" + entry.StartedAt.Ticks.ToString(CultureInfo.InvariantCulture);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    internal static async Task<LastFmHistoryBatch> ReadAsync(ILastFmHistorySource source, bool includeOlder,
        DateTimeOffset from, DateTimeOffset to, HashSet<string> pendingKeys, CancellationToken token,
        ILastFmStore? store = null, string account = "")
    {
        int tooOld = includeOlder ? 0 : await source.CountOlderAsync(from.UtcDateTime, token).ConfigureAwait(false);
        int ineligible = 0, deferred = 0, alreadyPresent = 0;
        var candidates = new List<LastFmHistoryCandidate>();
        // Recent records retain priority, so a large old library cannot crowd expiring plays out of the queue.
        await ReadRangeAsync(from.UtcDateTime, to.UtcDateTime, false).ConfigureAwait(false);
        if (includeOlder) await ReadRangeAsync(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), from.UtcDateTime.AddTicks(-1), true).ConfigureAwait(false);
        candidates.Sort((left, right) => left.Retimed != right.Retimed ? left.Retimed.CompareTo(right.Retimed) : left.StartedAt.CompareTo(right.StartedAt));
        return new(candidates, alreadyPresent, tooOld, ineligible, deferred);

        async Task ReadRangeAsync(DateTime start, DateTime end, bool retimed)
        {
            var rows = new List<LastFmHistoryEntry>(500);
            await foreach (var entry in source.ReadAsync(start, end, token).ConfigureAwait(false))
            {
                rows.Add(entry);
                if (rows.Count == 500) await FlushAsync().ConfigureAwait(false);
            }
            if (rows.Count != 0) await FlushAsync().ConfigureAwait(false);

            async Task FlushAsync()
            {
                var keys = new List<string>(rows.Count);
                foreach (var entry in rows) keys.Add(SourceKey(entry));
                var mappings = store is null ? null : await store.ReadMappingsAsync(account, keys, token).ConfigureAwait(false);
                for (int i = 0; i < rows.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = rows[i];
                    string key = keys[i];
                    if (pendingKeys.Contains(key) || (mappings?.TryGetValue(key, out var mapping) == true && mapping.Accepted)) { alreadyPresent++; continue; }
                    var track = QualifiedTrack(entry);
                    long timestamp = new DateTimeOffset(AsUtc(entry.StartedAt)).ToUnixTimeSeconds();
                    if (track is null || timestamp <= 0) { ineligible++; continue; }
                    if (candidates.Count >= 100000) { deferred++; continue; }
                    candidates.Add(new(track, timestamp, key, retimed));
                }
                rows.Clear();
            }
        }
    }

    internal static LastFmTrack? QualifiedTrack(LastFmHistoryEntry entry)
    {
        double duration = entry.TotalDurationMs;
        double listened = entry.DurationPlayedMs;
        double wall = (AsUtc(entry.EndedAt) - AsUtc(entry.StartedAt)).TotalMilliseconds;
        string title = entry.Title?.Trim() ?? "";
        string artist = entry.Artist?.Trim() ?? "";
        if (!double.IsFinite(duration) || !double.IsFinite(listened) || duration <= 30000
            || Math.Min(listened, wall) < Math.Min(duration / 2, 240000)
            || title.Length is 0 or > 512 || artist.Length is 0 or > 512) return null;
        string album = entry.Album?.Trim() ?? "";
        if (album.Length > 1024) album = album[..1024];
        return new(title, artist, album, (int)Math.Clamp(Math.Ceiling(duration / 1000), 0, int.MaxValue));
    }
}

/// <summary>Unique, elapsed seconds in the importing user's current local day. Never allocates future times.</summary>
internal sealed class LastFmImportDay
{
    private readonly HashSet<long> _reserved = [];
    private long _next;
    private readonly long _last;
    internal LastFmImportDay(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var local = zone is null ? now : TimeZoneInfo.ConvertTime(now, zone);
        var midnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        if (zone is null) _next = new DateTimeOffset(midnight, local.Offset).ToUnixTimeSeconds();
        else
        {
            // Some zones advance at midnight. Start at the first valid local minute of that day.
            while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
            if (zone.IsAmbiguousTime(midnight))
            {
                var offsets = zone.GetAmbiguousTimeOffsets(midnight);
                var earliestOffset = offsets[0] > offsets[1] ? offsets[0] : offsets[1];
                _next = new DateTimeOffset(midnight, earliestOffset).ToUnixTimeSeconds();
            }
            else _next = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone)).ToUnixTimeSeconds();
        }
        _last = now.ToUnixTimeSeconds();
    }
    internal void Reserve(long timestamp) => _reserved.Add(timestamp);
    internal bool TryAllocate(out long timestamp)
    {
        while (_next <= _last)
        {
            timestamp = _next++;
            if (_reserved.Add(timestamp)) return true;
        }
        timestamp = 0;
        return false;
    }
}

/// <summary>One-to-one matches prevent one remote play from swallowing repeated local listens.</summary>
internal sealed class LastFmHistoryIndex
{
    private readonly Dictionary<string, SortedSet<long>> _tracks = new(StringComparer.Ordinal);
    internal void Add(string key, long timestamp)
    {
        if (!_tracks.TryGetValue(key, out var times)) _tracks.Add(key, times = []);
        times.Add(timestamp);
    }

    internal bool Take(string key, long timestamp) => TryTake(key, timestamp, out _);

    internal bool TryTake(string key, long timestamp, out long matched)
    {
        matched = 0;
        if (!_tracks.TryGetValue(key, out var times)) return false;
        // Local statistics start just before decoder confirmation. Keep that bounded discrepancy
        // out of the duplicate history imported by the first Last.fm release.
        long? closest = null;
        foreach (long candidate in times.GetViewBetween(timestamp - 10, timestamp + 10))
            if (closest is null || Math.Abs(candidate - timestamp) < Math.Abs(closest.Value - timestamp)) closest = candidate;
        if (closest is not long match || !times.Remove(match)) return false;
        matched = match;
        return true;
    }

    internal bool TakeExact(string key, long timestamp) => _tracks.TryGetValue(key, out var times) && times.Remove(timestamp);
}
