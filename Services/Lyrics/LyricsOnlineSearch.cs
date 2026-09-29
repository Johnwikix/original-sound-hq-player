using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.WebService;

namespace WinUIMusicPlayer.Services.Lyrics;

/// <summary>Automatic and explicit searches share cancellation, provider state and validated results.</summary>
public sealed class LyricsOnlineSearch(MusicDatabaseService database, LrcService service, LyricsParser parser, ILogger<LyricsOnlineSearch> logger) : IDisposable
{
    private readonly object _automaticLock = new();
    private CancellationTokenSource _automatic = new();
    private bool _disposed;
    public void CancelAutomaticSearch()
    {
        lock (_automaticLock)
        {
            if (_disposed) return;
            _automatic.Cancel();
            _automatic.Dispose();
            _automatic = new();
        }
    }
    public void Dispose()
    {
        lock (_automaticLock)
        {
            if (_disposed) return;
            _disposed = true;
            _automatic.Cancel();
            _automatic.Dispose();
        }
        _gate.Dispose(); // Host disposal follows ApplicationTasks.DrainAsync.
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LyricsSearchCircuitBreaker[] _circuits = [new(3, 600000), new(3, 600000), new(3, 600000)];
    private readonly Dictionary<string, LyricsSearchStateRecord> _external = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Channels = ["qq-word", "netease-line", "qq-line"];

    public async Task<LyricsDocument?> SearchAsync(Music music, bool force, CancellationToken token)
    {
        music = new Music { Id = music.Id, Path = music.Path, Title = music.Title ?? "", Author = music.Author ?? "",
            Album = music.Album ?? "", Duration = music.Duration, IsLrcSearched = music.IsLrcSearched, IsKrcSearched = music.IsKrcSearched };
        CancellationToken callerToken = token;
        CancellationTokenSource linked;
        lock (_automaticLock)
        {
            if (_disposed || (!force && !AppSettings.IsAutoLyricsEnabled)) return null;
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, force ? CancellationToken.None : _automatic.Token);
        }
        using var searchLifetime = linked;
        token = linked.Token;
        bool acquired = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{music.Title.Length}:{music.Title}{music.Author.Length}:{music.Author}{music.Album.Length}:{music.Album}:{music.Duration.Ticks}")));
            for (int i = 0; i < Channels.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string channel = Channels[i];
                string key = music.Path + "|" + channel;
                var search = music.Id > 0 ? await database.Lyrics.GetSearchAsync(music.Id, channel).ConfigureAwait(false)
                    : _external.GetValueOrDefault(key);
                if (search is null && (i == 0 ? music.IsKrcSearched : music.IsLrcSearched))
                {
                    await RecordAsync(music.Id, key, channel, hash, "LegacyCompleted").ConfigureAwait(false);
                    if (!force) continue;
                }
                if (!force && search?.QueryHash == hash && search.Status is "NoResult" or "LegacyCompleted") continue;
                int generation = 0;
                if (!force && !_circuits[i].TryBegin(Environment.TickCount64, out generation)) continue;
                LyricsSearchStatus? outcome = null;
                try
                {
                    var response = i switch
                    {
                        0 => await service.GetWordLyricsAsync(music, token).ConfigureAwait(false),
                        1 => await service.GetLyricsAsync(music, Lyricify.Lyrics.Searchers.Searchers.Netease, token).ConfigureAwait(false),
                        _ => await service.GetLyricsAsync(music, Lyricify.Lyrics.Searchers.Searchers.QQMusic, token).ConfigureAwait(false)
                    };
                    token.ThrowIfCancellationRequested();
                    outcome = response.Status;
                    if (response.Status == LyricsSearchStatus.Found)
                    {
                        LyricsDocument document;
                        try { document = parser.Import(response.Lyrics, response.Trans, token); }
                        catch (FormatException ex)
                        {
                            logger.LogWarning(ex, "Invalid provider translation: {Channel}", channel);
                            document = parser.Import(response.Lyrics, null, token);
                        }
                        if (!parser.HasLyrics(document, token)) { outcome = LyricsSearchStatus.NetworkError; continue; }
                        await RecordAsync(music.Id, key, channel, hash, "Found").ConfigureAwait(false);
                        return document;
                    }
                    if (response.Status == LyricsSearchStatus.NoResult)
                        await RecordAsync(music.Id, key, channel, hash, "NoResult").ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    outcome = LyricsSearchStatus.NetworkError;
                    logger.LogWarning(ex, "Lyrics provider failed: {Channel}", channel);
                }
                finally { if (!force) _circuits[i].Complete(generation, outcome, Environment.TickCount64); }
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !callerToken.IsCancellationRequested) { return null; }
        finally { if (acquired) _gate.Release(); }
    }

    private Task RecordAsync(int id, string key, string channel, string hash, string status)
    {
        if (id > 0) return database.Lyrics.SetSearchAsync(id, channel, hash, status);
        if (_external.Count >= 900) _external.Clear();
        _external[key] = new() { Channel = channel, QueryHash = hash, Status = status };
        return Task.CompletedTask;
    }
}
