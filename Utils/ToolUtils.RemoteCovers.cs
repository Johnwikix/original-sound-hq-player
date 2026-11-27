using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.WebService;

namespace WinUIMusicPlayer.Utils;

public partial class ToolUtils
{
    private static readonly SemaphoreSlim RemoteCoverMatchSlot = new(1, 1);
    private static readonly Lock RemoteCoverMatchState = new();
    private static CancellationTokenSource? _activeRemoteCoverMatch;

    internal static void CancelRemoteCoverMatch()
    {
        lock (RemoteCoverMatchState)
            _activeRemoteCoverMatch?.Cancel();
    }

    private static async Task<byte[]> GetRemoteRawImageAsync(Music music, bool isManual, CancellationToken token)
    {
        var version = RemoteCoverVersion.Capture(music);
        var library = App.Services.GetRequiredService<WebDavLibraryService>();
        var (bytes, canMatch) = await library.ReadCoverResultAsync(music, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (version != RemoteCoverVersion.Capture(music)) return [];
        if (bytes.Length != 0 || !canMatch || isManual || !AppSettings.IsAutoCoverEnabled ||
            !string.IsNullOrEmpty(version.OverridePath)) return bytes;

        // A slow search in the library must not indefinitely delay the now-playing cover.
        using var matching = CancellationTokenSource.CreateLinkedTokenSource(token);
        matching.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await RemoteCoverMatchSlot.WaitAsync(matching.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return [];
        }
        try
        {
            // Another cover consumer may have filled the cache while this request was waiting.
            if (version != RemoteCoverVersion.Capture(music)) return [];
            (bytes, canMatch) = await library.ReadCoverResultAsync(music, matching.Token).ConfigureAwait(false);
            matching.Token.ThrowIfCancellationRequested();
            if (version != RemoteCoverVersion.Capture(music)) return [];
            if (bytes.Length != 0 || !canMatch || !AppSettings.IsAutoCoverEnabled) return bytes;

            lock (RemoteCoverMatchState)
            {
                if (!AppSettings.IsAutoCoverEnabled) return [];
                _activeRemoteCoverMatch = matching;
            }
            try
            {
                var matched = await App.Services.GetRequiredService<LrcService>()
                    .GetMixedCoverImageAsync(music, matching.Token).ConfigureAwait(false);
                matching.Token.ThrowIfCancellationRequested();
                if (matched is not { Length: > 0 } || version != RemoteCoverVersion.Capture(music)) return [];
                await library.CacheMatchedCoverAsync(music, version.ImageHash, matched, matching.Token).ConfigureAwait(false);
                matching.Token.ThrowIfCancellationRequested();
                return version == RemoteCoverVersion.Capture(music) ? matched : [];
            }
            finally
            {
                lock (RemoteCoverMatchState)
                    _activeRemoteCoverMatch = null;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && matching.IsCancellationRequested)
        {
            return [];
        }
        finally
        {
            RemoteCoverMatchSlot.Release();
        }
    }

    private readonly record struct RemoteCoverVersion(string ImageHash, string? OverridePath, string Album, string Title, string Author)
    {
        public static RemoteCoverVersion Capture(Music music)
            => new(music.ImageHash ?? "", music.CoverOverridePath, music.Album, music.Title, music.Author);
    }
}
