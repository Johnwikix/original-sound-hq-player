using OriginalSound.Plugin;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Plugins;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.WebService;


/// <summary>Compatibility facade for existing consumers; all platform requests execute in plugin workers.</summary>
public sealed class LrcService(PluginManager plugins) : IDisposable
{
    private readonly ConcurrentDictionary<(string Provider, string Track, bool Words), long> _misses = new();
    private readonly ConcurrentDictionary<string, LyricsSearchCircuitBreaker> _circuits = new();
    public bool IsAvailable => plugins.HasLyrics;
    public static TrackMetadata Metadata(Music music) => new(music.Id > 0 ? music.Id.ToString() : music.Path,
        music.Title, music.Author, music.Album, (long)music.Duration.TotalMilliseconds, AppData.SystemLanguage);
    public Task<(string Lyrics, string Trans, LyricsSearchStatus Status)> GetMixedLyricsAsync(Music music, CancellationToken cancellationToken = default)
        => SearchAsync(music, false, cancellationToken);
    public Task<(string Lyrics, string Trans, LyricsSearchStatus Status)> GetKrcLyricsAsync(Music music, CancellationToken cancellationToken = default)
        => SearchAsync(music, true, cancellationToken);
    private async Task<(string, string, LyricsSearchStatus)> SearchAsync(Music music, bool words, CancellationToken ct)
    {
        bool failed = false;
        var providers = plugins.Providers("lyrics");
        if (providers.Length == 0) return ("", "", LyricsSearchStatus.Unavailable);
        var metadata = Metadata(music);
        // Legacy song-level Is* Searched flags cannot describe which provider was queried.
        // Use bounded, per-provider negative caching; a restart or newly enabled provider can search again.
        string key = $"{metadata.Id}\0{metadata.Title}\0{metadata.Artist}\0{metadata.Album}";
        foreach (string provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            if (_misses.TryGetValue((provider, key, words), out long expiry) && expiry > Environment.TickCount64) continue;
            var circuit = _circuits.GetOrAdd(provider, static _ => new LyricsSearchCircuitBreaker(3, 60000));
            if (!circuit.TryBegin(Environment.TickCount64, out int generation)) { failed = true; continue; }
            PluginReply reply;
            LyricsSearchStatus? outcome = null;
            try
            {
                reply = await plugins.InvokeAsync(provider, new() { Method = "lyrics", Track = metadata, WordSynced = words }, ct);
                outcome = reply.Result == PluginResult.NetworkError ? LyricsSearchStatus.NetworkError
                    : reply.Result == PluginResult.Unavailable ? null : LyricsSearchStatus.NoResult;
            }
            finally { circuit.Complete(generation, outcome, Environment.TickCount64); }
            ct.ThrowIfCancellationRequested();
            if (reply.Result == PluginResult.Found && reply.Lyrics.Length > 0)
            {
                var lyric = reply.Lyrics[0];
                return (lyric.Text, lyric.Translation, LyricsSearchStatus.Found);
            }
            if (reply.Result == PluginResult.NoResult)
            {
                if (_misses.Count >= 2048) _misses.Clear();
                _misses[(provider, key, words)] = Environment.TickCount64 + 60 * 60 * 1000;
            }
            else
            {
                failed = true;
            }
        }
        return ("", "", failed ? LyricsSearchStatus.NetworkError : LyricsSearchStatus.NoResult);
    }
    public async Task<byte[]?> GetMixedCoverImageAsync(Music music, CancellationToken cancellationToken = default)
    {
        if (AppData.UnknownAlbums.Contains(music.Album)) return null;
        foreach (string provider in plugins.Providers("cover"))
        {
            var reply = await plugins.InvokeAsync(provider, new() { Method = "cover", Track = Metadata(music) }, cancellationToken);
            if (reply.Result == PluginResult.Found && reply.Cover is { Length: > 0 }) return reply.Cover;
        }
        return null;
    }
    public void Dispose() { _misses.Clear(); _circuits.Clear(); }
}
