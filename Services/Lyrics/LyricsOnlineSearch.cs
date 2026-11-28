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

    private const string KugouWord = "kugou-word";
    private const string QqWord = "qq-word";
    private const string NeteaseLine = "netease-line";
    private const string LrcLibLine = "lrclib-line";
    private static readonly string[] Channels = [QqWord, KugouWord, NeteaseLine, LrcLibLine];
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LyricsSearchCircuitBreaker[] _circuits = Array.ConvertAll(Channels, static _ => new LyricsSearchCircuitBreaker(3, 600000));
    private readonly Dictionary<string, LyricsSearchStateRecord> _external = new(StringComparer.OrdinalIgnoreCase);

    public async Task<LyricsDocument?> SearchAsync(Music music, bool force, CancellationToken token)
    {
        music = new Music { Id = music.Id, CueTrackNo = music.CueTrackNo, CuePath = music.CuePath, Path = music.Path, Title = music.Title ?? "", Author = music.Author ?? "",
            Album = music.Album ?? "", Duration = music.Duration };
        string language = AppData.SystemLanguage;
        bool preferChineseTranslation = language.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("zh-", StringComparison.OrdinalIgnoreCase);
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
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v2:{music.Title.Length}:{music.Title}{music.Author.Length}:{music.Author}{music.Album.Length}:{music.Album}:{music.Duration.Ticks}")));
            LyricsDocument? fallback = null;
            for (int i = 0; i < Channels.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string channel = Channels[i];
                // LRCLIB 只提供原文。已有可用原文时，查它无法改善译文。
                if (channel == LrcLibLine && fallback is not null) return fallback;
                string key = music.LocalTrackKey + "|" + channel;
                var search = music.Id > 0 ? await database.Lyrics.GetSearchAsync(music.Id, channel).ConfigureAwait(false)
                    : _external.GetValueOrDefault(key);
                token.ThrowIfCancellationRequested();
                // 旧搜索状态使用了不同的接口和匹配规则。按歌曲重新验证，无需重写曲库。
                if (!force && search?.QueryHash == hash && search.Status == "NoResult") continue;
                int generation = 0;
                if (!force && !_circuits[i].TryBegin(Environment.TickCount64, out generation)) continue;
                LyricsSearchStatus? outcome = null;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(ProviderTimeout);
                    var providerToken = deadline.Token;
                    var response = channel switch
                    {
                        QqWord => await service.GetWordLyricsAsync(music, providerToken).ConfigureAwait(false),
                        KugouWord => await service.GetKugouWordLyricsAsync(music, providerToken).ConfigureAwait(false),
                        NeteaseLine => await service.GetLyricsAsync(music, Lyricify.Lyrics.Searchers.Searchers.Netease, providerToken).ConfigureAwait(false),
                        LrcLibLine => await service.GetLrcLibLyricsAsync(music, providerToken).ConfigureAwait(false),
                        _ => throw new InvalidOperationException($"Unknown lyrics channel: {channel}"),
                    };
                    providerToken.ThrowIfCancellationRequested();
                    outcome = response.Status;
                    if (response.Status == LyricsSearchStatus.Found)
                    {
                        LyricsDocument document;
                        try { document = parser.Import(response.Lyrics, response.Trans, providerToken, preferredLanguage: language); }
                        catch (FormatException ex)
                        {
                            logger.LogWarning(ex, "Invalid provider translation: {Channel}", channel);
                            document = parser.Import(response.Lyrics, token: providerToken, preferredLanguage: language, extractEmbeddedTranslation: false);
                        }
                        if (!parser.HasLyrics(document, providerToken))
                        {
                            outcome = LyricsSearchStatus.NetworkError;
                            continue;
                        }
                        // 逐字请求也可能返回普通 LRC，不能让它抢占后续提供方的逐字候选。
                        if ((channel == KugouWord && document.Original.Format != LyricsFormat.Krc) ||
                            (channel == QqWord && document.Original.Format != LyricsFormat.Qrc))
                            outcome = LyricsSearchStatus.NoResult;
                        else
                        {
                            providerToken.ThrowIfCancellationRequested();
                            await RecordAsync(music.Id, key, channel, hash, "Found").ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (!preferChineseTranslation || channel == LrcLibLine ||
                                LyricsLanguagePolicy.DetectLines(parser.GetAlignedTranslationLines(document, providerToken)) == LyricsLanguage.Mandarin)
                                return document;

                            // 按逐字到逐行的顺序寻找译文，首个可用结果用于回退。
                            fallback ??= document;
                        }
                    }
                    if (outcome == LyricsSearchStatus.NoResult)
                    {
                        providerToken.ThrowIfCancellationRequested();
                        await RecordAsync(music.Id, key, channel, hash, "NoResult").ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    outcome = LyricsSearchStatus.NetworkError;
                    logger.LogWarning(ex, "Lyrics provider failed: {Channel}", channel);
                }
                finally { if (!force) _circuits[i].Complete(generation, outcome, Environment.TickCount64); }
            }
            token.ThrowIfCancellationRequested();
            return fallback;
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
