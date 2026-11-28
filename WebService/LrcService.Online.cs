using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Providers.Web;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;
using KrcDecrypter = Lyricify.Lyrics.Decrypter.Krc.Decrypter;

namespace WinUIMusicPlayer.WebService;

public partial class LrcService
{
    private const int DurationToleranceMs = 3500;
    private static readonly string[] ArtistSeparators = [", ", " / ", "、", "，", ";", "；"];
    private static readonly Regex VersionTag = new(
        @"(?:[(\[（【]\s*|\s[-–—]\s+|^)(?:\d{4}\s+)?(?<tag>taylor['’]s version|live\b|acoustic\b|instrumental\b|karaoke\b|remaster(?:ed)?\b|remix\b|radio edit\b|extended\b|demo\b|re[- ]?record(?:ed|ing)?\b|现场|現場|伴奏|纯音乐|純音樂|重制|重製|重录|重錄)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly QQMusicSearcher _qqLyricsSearcher = new(preferMobile: true);
    private readonly NeteaseSearcher _neteaseLyricsSearcher = new(preferNew: true);

    public async Task<(string Lyrics, string Trans, LyricsSearchStatus Status)> GetKugouWordLyricsAsync(
        Music music, CancellationToken cancellationToken)
    {
        try
        {
            var query = CreateLyricsQuery(music);
            var match = await FindLyricsTrackAsync(SearcherHelper.KugouSearcher, query,
                CompareHelper.MatchType.PrettyHigh, cancellationToken).ConfigureAwait(false);
            if (match is not KugouSearchResult track)
                return ("", "", LyricsSearchStatus.NoResult);

            var api = ProviderHelper.KugouApi;
            var response = await api.GetSearchLyrics($"{string.Join(", ", track.Artists)} - {track.Title}", track.DurationMs,
                track.Hash, cancellationToken).ConfigureAwait(false);
            if (response is null || response.Status is not (200 or 1) || response.ErrorCode is not (0 or 200))
                throw new InvalidOperationException("酷狗歌词搜索服务返回失败状态。");

            var candidate = response.Candidates?.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.AccessKey) &&
                !HasDurationConflict(query.DurationMs, item.Duration));
            if (candidate is null)
                return ("", "", LyricsSearchStatus.NoResult);

            var download = await api.DownloadLyrics(candidate.Id, candidate.AccessKey, cancellationToken).ConfigureAwait(false);
            if (download is null || download.Status is not (200 or 1) || download.ErrorCode != 0)
                throw new InvalidOperationException("酷狗歌词下载服务返回失败状态。");
            if (string.IsNullOrWhiteSpace(download.Content))
                return ("", "", LyricsSearchStatus.NoResult);

            cancellationToken.ThrowIfCancellationRequested();
            string? lyrics = KrcDecrypter.DecryptLyrics(download.Content, LyricsParser.MaxContentLength * 3 + 3);
            return string.IsNullOrWhiteSpace(lyrics)
                ? ("", "", LyricsSearchStatus.NoResult)
                : (lyrics, "", LyricsSearchStatus.Found);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "酷狗逐字歌词获取失败");
            return ("", "", LyricsSearchStatus.NetworkError);
        }
    }

    public async Task<(string Lyrics, string Trans, LyricsSearchStatus Status)> GetLrcLibLyricsAsync(
        Music music, CancellationToken cancellationToken)
    {
        try
        {
            var query = CreateLyricsQuery(music);
            string url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(query.Title ?? "")}&artist_name={Uri.EscapeDataString(query.Artist ?? "")}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("OriginalSoundPlayer");
            using var response = await BaseApi.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);

            var bestAlbumScore = -1;
            double closestDuration = double.MaxValue;
            string? bestLyrics = null;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.TryGetProperty("syncedLyrics", out var synced) || synced.ValueKind == JsonValueKind.Null)
                    continue;
                string? lyrics = synced.GetString();
                if (string.IsNullOrWhiteSpace(lyrics) || lyrics.Length > LyricsParser.MaxContentLength)
                    continue;

                string? title = item.GetProperty("trackName").GetString();
                string[] artists = SplitArtists(item.GetProperty("artistName").GetString());
                string? album = item.GetProperty("albumName").GetString();
                double milliseconds = item.GetProperty("duration").GetDouble() * 1000;
                int? duration = milliseconds is > 0 and <= int.MaxValue ? (int)milliseconds : null;
                if (!MatchesLyricsTrack(query, title, artists, duration, null))
                    continue;

                int albumScore = CompareHelper.CompareName(query.Album, album).GetMatchScore();
                double durationDifference = query.DurationMs is > 0 && duration is > 0
                    ? Math.Abs(query.DurationMs.Value - duration.Value)
                    : double.MaxValue;
                if (albumScore < bestAlbumScore || (albumScore == bestAlbumScore && durationDifference >= closestDuration))
                    continue;

                bestLyrics = lyrics;
                bestAlbumScore = albumScore;
                closestDuration = durationDifference;
            }
            return bestLyrics is not null
                ? (bestLyrics, "", LyricsSearchStatus.Found)
                : ("", "", LyricsSearchStatus.NoResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "LRCLIB 歌词获取失败");
            return ("", "", LyricsSearchStatus.NetworkError);
        }
    }

    private static TrackMultiArtistMetadata CreateLyricsQuery(Music music)
        => new()
        {
            Title = music.Title,
            Artists = [.. SplitArtists(music.Author)],
            Album = music.Album,
            DurationMs = music.Duration.TotalMilliseconds is > 0 and <= int.MaxValue
                ? (int)music.Duration.TotalMilliseconds
                : null,
        };

    private static string[] SplitArtists(string? artist)
        => artist?.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static Task<ISearchResult?> FindLyricsTrackAsync(Searcher searcher, TrackMultiArtistMetadata query,
        CompareHelper.MatchType minimumMatch, CancellationToken cancellationToken)
        => searcher.SearchForResult(query, minimumMatch, result =>
            MatchesLyricsTrack(query, result.Title, result.Artists, result.DurationMs,
                (result as QQMusicSearchResult)?.Subtitle), cancellationToken);

    private static bool MatchesLyricsTrack(TrackMultiArtistMetadata query, string? title,
        string[] artists, int? duration, string? subtitle)
    {
        if (HasDurationConflict(query.DurationMs, duration) ||
            GetVersionTag(query.Title) != (GetVersionTag(title) ?? GetVersionTag(subtitle)))
            return false;

        int titleScore = CompareHelper.CompareName(query.Title, title).GetMatchScore();
        int artistScore = CompareHelper.CompareArtist(query.Artists, artists).GetMatchScore();
        return query.DurationMs is > 0 && duration is > 0
            ? titleScore >= 4 && artistScore >= 4
            : titleScore >= 6 && artistScore >= 5;
    }

    private static bool HasDurationConflict(int? expected, int? actual)
        => expected is > 0 && actual is > 0 && Math.Abs(expected.Value - actual.Value) >= DurationToleranceMs;

    private static string? GetVersionTag(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var match = VersionTag.Match(title);
        return match.Success ? match.Groups["tag"].Value.ToLowerInvariant() switch
        {
            "现场" or "現場" => "live",
            "伴奏" or "纯音乐" or "純音樂" or "karaoke" => "instrumental",
            "remastered" or "重制" or "重製" => "remaster",
            "重录" or "重錄" or "re-recorded" or "re-recording" or "rerecorded" or "rerecording" => "rerecord",
            "taylor’s version" => "taylor's version",
            string tag => tag,
        } : null;
    }
}
