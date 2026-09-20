using BassPlayerIpc.Shared;
using OriginalSound.Plugin;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Plugins;

/// <summary>Future music providers resolve stable identities at playback time. Signed URLs are never library paths or persisted queue entries.</summary>
public sealed class StreamingPlaybackService(PluginManager plugins)
{
    public StreamingClient Player { get; } = new();
    public static PlaybackEntry FromLocal(Music music) => new(new("local", music.Id > 0 ? music.Id.ToString() : music.Path),
        WebService.LrcService.Metadata(music), music.Path, true);

    public async Task<StreamReply> PrepareAsync(PlaybackEntry entry, Guid sessionId, CancellationToken ct)
    {
        PlaybackSource source;
        if (entry.IsLocal)
            source = new() { Kind = PlaybackSourceKind.LocalFile, Location = entry.ResourceId, ResourceId = entry.Identity.TrackId };
        else
        {
            var reply = await plugins.InvokeAsync(entry.Identity.ProviderId,
                new() { Method = "resolve", ResourceId = entry.ResourceId, Track = entry.Metadata }, ct);
            if (reply.Result != PluginResult.Found || reply.Source is not { } resolved)
                return new() { SessionId = sessionId, Error = "SourceUnavailable", Phase = StreamPhase.Failed };
            source = new()
            {
                Kind = PlaybackSourceKind.Http, ResourceId = entry.ResourceId, Location = resolved.Url,
                Headers = resolved.Headers, ExpiresAt = resolved.ExpiresAt, CanSeek = resolved.CanSeek, IsLive = resolved.IsLive
            };
        }
        source.Validate();
        try { return await Player.PrepareAsync(source, sessionId, ct); }
        catch (OperationCanceledException)
        {
            // A canceled client wait does not prove that the server rejected the prepare request.
            try { await Player.StopAsync(sessionId); } catch { }
            throw;
        }
    }
}
