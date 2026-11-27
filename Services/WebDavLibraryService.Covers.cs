using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.RemoteSources;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services;

public sealed partial class WebDavLibraryService
{
    private const int CatalogCoverLimit = 8 * 1024 * 1024;

    private async Task<(byte[] Bytes, bool CanMatch)> ReadJellyfinCoverAsync(Music music, WebDavSource source, RemoteTrack track, CancellationToken token)
    {
        string key = JellyfinCoverKey(music, track);
        string root = AppSettings.MusicCoverCache;
        string raw = WebDavCachePaths.Cover(root, key);
        var original = new FileInfo(raw);
        if (original.Exists && original.Length is > 0 and <= CatalogCoverLimit)
            return (await File.ReadAllBytesAsync(raw, token).ConfigureAwait(false), false);

        string missing = WebDavCachePaths.MissingCover(root, key);
        if (string.IsNullOrEmpty(track.CoverId) || File.Exists(missing))
            return await ReadJellyfinMatchedCoverAsync(root, key, source.Id, track.Missing, token).ConfigureAwait(false);
        if (track.Missing || IsSourceOffline(source.Id)) return ([], false);

        var (bytes, notFound) = await ReadCatalogCoverAsync(source, track, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (notFound)
        {
            // Older versions used an empty raw file for multiple outcomes. This marker represents a confirmed miss only.
            await StoreJellyfinCoverCacheAsync(root, missing, [], token).ConfigureAwait(false);
            return await ReadJellyfinMatchedCoverAsync(root, key, source.Id, track.Missing, token).ConfigureAwait(false);
        }
        if (bytes.Length > 0)
            await StoreJellyfinCoverCacheAsync(root, raw, bytes, token).ConfigureAwait(false);
        return (bytes, false);
    }

    private async Task<(byte[] Bytes, bool CanMatch)> ReadJellyfinMatchedCoverAsync(string root, string key, int sourceId, bool trackMissing,
        CancellationToken token)
    {
        string matched = WebDavCachePaths.MatchedCover(root, key);
        var file = new FileInfo(matched);
        if (file.Exists && file.Length is > 0 and <= CatalogCoverLimit)
            return (await File.ReadAllBytesAsync(matched, token).ConfigureAwait(false), false);
        token.ThrowIfCancellationRequested();
        return ([], !trackMissing && !IsSourceOffline(sourceId));
    }

    /// <summary>Stores a matched image beside the confirmed server miss while the music and cover revision still agree.</summary>
    public async Task CacheMatchedCoverAsync(Music music, string expectedImageHash, byte[] picture, CancellationToken token)
    {
        if (picture.Length is 0 or > CatalogCoverLimit) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, token);
        await _coverSlot.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var (source, track) = await ResolveAsync(music).ConfigureAwait(false);
            if (source.Kind != RemoteSourceKind.Jellyfin || track.Missing || IsSourceOffline(source.Id) ||
                (music.ImageHash ?? "") != expectedImageHash || !string.IsNullOrEmpty(music.CoverOverridePath)) return;
            string root = AppSettings.MusicCoverCache;
            string key = JellyfinCoverKey(music, track);
            var original = new FileInfo(WebDavCachePaths.Cover(root, key));
            if (original.Exists && original.Length > 0) return;
            if (!string.IsNullOrEmpty(track.CoverId) && !File.Exists(WebDavCachePaths.MissingCover(root, key))) return;
            await StoreJellyfinCoverCacheAsync(root, WebDavCachePaths.MatchedCover(root, key), picture, linked.Token).ConfigureAwait(false);
        }
        catch (IOException) { /* The caller can still use the newly matched bytes for the current view. */ }
        finally { _coverSlot.Release(); }
    }

    private static string JellyfinCoverKey(Music music, RemoteTrack track) => !string.IsNullOrEmpty(music.ImageHash)
        ? music.ImageHash
        : WebDavCachePaths.GetJellyfinImageHash(track.SourceId, track.Href, "", music.Title, music.Author, music.Album);

    private async Task StoreJellyfinCoverCacheAsync(string root, string path, byte[] bytes, CancellationToken token)
    {
        try
        {
            await PlaybackCoverCache.StoreAsync(path, bytes, token).ConfigureAwait(false);
            if (bytes.Length > 0) TrimCovers(WebDavCachePaths.Covers(root), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Jellyfin cover cache write failed: {ErrorType}", ex.GetType().Name);
        }
    }
}
