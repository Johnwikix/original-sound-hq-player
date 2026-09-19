using AudioPlayer.Interop;
using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

public sealed partial class PlaybackEngine
{
    private string? _surroundEndpoint;
    private bool _surroundFallback;
    private bool _surroundFailed;
    private long _surroundFailureSequence;
    private bool _surroundFailureStopped;
    private bool _surroundFailureReported;

    private bool IsSurroundSession(Session? session) => ExperimentalSurround51 && !_atmosUseSharedPcm
        && session is { Kind: RenderKind.Pcm, Channels: 6, ChannelMask: 0x3F or 0x60F };

    private void ResetSurroundAttempt()
    {
        _surroundEndpoint = null;
        _surroundFallback = false;
        _surroundFailed = false;
        _surroundFailureReported = false;
    }

    private IAudioOutput? CreateSurroundOutput(Session session)
    {
        _surroundEndpoint ??= WasapiDeviceList.ResolveStableDeviceId(
            OutputMode == "DirectSound" ? -1 : BassOutputDeviceId,
            OutputMode == "DirectSound" ? null : WasapiEndpointId);
        if (string.IsNullOrEmpty(_surroundEndpoint)) return null;
        var output = new WasapiOutput(true, OutputMode == "WasapiExclusivePush");
        if (output.Start(-1, Latency, session, 1, _surroundEndpoint, id => PrepareOutputDsp(session, id))) return output;
        output.Dispose();
        return null;
    }

    private bool RebuildSurroundFallback(Session failed)
    {
        if (!ReferenceEquals(failed, _session)) return false;
        long position = GetTimeProgress().currentMs;
        string? url = MusicUrl;
        _surroundFailed = true;
        _surroundFallback = true;
        DisposeSession();
        if (string.IsNullOrEmpty(url)) return false;
        SetSession(OpenSession(url, kindOverride: RenderKind.Pcm));
        if (_session == null) return false;
        _session.RequestSeek(position);
        return true;
    }

    private void FailSurroundOutput(Session failed)
    {
        _surroundFailed = true;
        if (OutputMode != "ASIO" && string.IsNullOrEmpty(_surroundEndpoint))
        {
            StopSurroundForDeviceChange();
            return;
        }
        if (RebuildSurroundFallback(failed)) StartOutputAndPlay();
        else StopAndNotifyLocked();
    }

    private void CompleteSurroundStart(bool success)
    {
        if (success && IsSurroundSession(_session)) _surroundFailed = false;
        if (!_surroundFailed) return;
        if (!_surroundFailureReported || _surroundFailureStopped != !success)
        {
            _surroundFailureReported = true;
            _surroundFailureStopped = !success;
            _surroundFailureSequence++;
        }
    }

    private void StopSurroundForDeviceChange()
    {
        long position = GetTimeProgress().currentMs;
        _recovery = null;
        _surroundFailed = true;
        _output?.Dispose();
        _output = null;
        _session?.RequestSeek(position);
        StopAndNotifyLocked();
    }

    private SurroundPlaybackStatus GetSurroundStatus()
    {
        if (!ExperimentalSurround51) return SurroundPlaybackStatus.Off;
        if (_surroundFailed) return _surroundFailureStopped ? SurroundPlaybackStatus.Stopped : SurroundPlaybackStatus.Fallback;
        if (!IsSurroundSession(_session)) return SurroundPlaybackStatus.Waiting;
        return _output is { IsFailed: false } ? SurroundPlaybackStatus.Active : SurroundPlaybackStatus.Ready;
    }
}
