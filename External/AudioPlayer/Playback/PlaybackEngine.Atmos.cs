using AudioPlayer.Interop;
using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

public sealed partial class PlaybackEngine
{
    public string? AtmosEndpointId;
    private string? _atmosResolvedEndpoint;
    private bool _atmosUseSharedPcm;
    private bool _atmosFailureReported;
    private AtmosPlaybackStatus _atmosStatus;
    private AtmosFailure _atmosReason;
    private long _atmosFailureSequence;
    private AtmosFailure _lastAtmosFailure;
    private bool _atmosFailureStopped;

    // Reset only for a new track or an explicit output-settings change. Recovery,
    // pause and seek must not repeatedly reacquire exclusive mode after a failure.
    private void ResetAtmosAttempt()
    {
        _atmosResolvedEndpoint = null;
        _atmosUseSharedPcm = false;
        _atmosFailureReported = false;
        _atmosReason = AtmosFailure.None;
        _atmosStatus = ExperimentalAtmosPassthrough ? AtmosPlaybackStatus.Waiting : AtmosPlaybackStatus.Off;
    }

    private bool ResolveAtmosEndpoint()
    {
        // Resolve once and pin the ID through capability negotiation, initialization
        // and PCM fallback. Never fall back to an unrelated default speaker.
        if (string.IsNullOrEmpty(_atmosResolvedEndpoint))
        {
            string? requested = !string.IsNullOrEmpty(AtmosEndpointId) ? AtmosEndpointId
                : OutputMode == "DirectSound" ? null : WasapiEndpointId;
            int index = !string.IsNullOrEmpty(requested) || OutputMode is "DirectSound" or "ASIO" ? -1 : BassOutputDeviceId;
            _atmosResolvedEndpoint = WasapiDeviceList.ResolveStableDeviceId(index, requested);
        }
        if (string.IsNullOrEmpty(_atmosResolvedEndpoint))
        {
            _atmosReason = AtmosFailure.DeviceUnavailable;
            return false;
        }
        return true;
    }

    private IAudioOutput? CreateAtmosOutput(Session session)
    {
        if (!ResolveAtmosEndpoint()) return null;
        var output = new WasapiOutput(true, OutputMode == "WasapiExclusivePush");
        if (output.Start(-1, Latency, session, 1, _atmosResolvedEndpoint, id => PrepareOutputDsp(session, id)))
        {
            _atmosReason = AtmosFailure.None;
            return output;
        }
        _atmosReason = ClassifyAtmosFailure(output.LastError);
        output.Dispose();
        return null;
    }

    internal static AtmosFailure ClassifyAtmosFailure(int error) => error switch
    {
        WasapiTypes.AudclntEUnsupportedFormat => AtmosFailure.UnsupportedFormat,
        WasapiTypes.AudclntEDeviceInUse => AtmosFailure.DeviceBusy,
        WasapiTypes.AudclntEExclusiveModeNotAllowed => AtmosFailure.ExclusiveDenied,
        WasapiTypes.AudclntEDeviceInvalidated => AtmosFailure.DeviceUnavailable,
        _ => AtmosFailure.OutputFailed
    };

    private void CompleteAtmosStart(bool success)
    {
        if (_atmosReason != AtmosFailure.None)
        {
            _atmosStatus = success ? AtmosPlaybackStatus.PcmFallback : AtmosPlaybackStatus.Stopped;
            if (!_atmosFailureReported || _lastAtmosFailure != _atmosReason || _atmosFailureStopped != !success)
            {
                _atmosFailureReported = true;
                _lastAtmosFailure = _atmosReason;
                _atmosFailureStopped = !success;
                _atmosFailureSequence++;
            }
        }
        else if (_session?.Kind == RenderKind.Eac3)
            _atmosStatus = success ? AtmosPlaybackStatus.Active : AtmosPlaybackStatus.Ready;
        QueueDspState();
    }

    private bool RebuildAtmosFallback(Session failed)
    {
        if (!ReferenceEquals(failed, _session)) return false;
        long position = GetTimeProgress().currentMs;
        string? url = MusicUrl;
        _atmosUseSharedPcm = true;
        DisposeSession();
        if (string.IsNullOrEmpty(url)) return false;
        SetSession(OpenSession(url, kindOverride: RenderKind.Pcm));
        if (_session == null) return false;
        _session.RequestSeek(position);
        return true;
    }

    private void FailAtmosOutput(Session failed)
    {
        if (_atmosReason == AtmosFailure.None) _atmosReason = AtmosFailure.OutputFailed;
        if (_atmosReason == AtmosFailure.DeviceUnavailable || string.IsNullOrEmpty(_atmosResolvedEndpoint))
        {
            StopAtmosForDeviceChange();
            return;
        }
        if (RebuildAtmosFallback(failed)) StartOutputAndPlay();
        else StopAndNotifyLocked();
    }

    private void StopAtmosForDeviceChange()
    {
        long position = GetTimeProgress().currentMs;
        _recovery = null;
        _atmosReason = AtmosFailure.DeviceUnavailable;
        _output?.Dispose();
        _output = null;
        _session?.RequestSeek(position);
        StopAndNotifyLocked();
        CompleteAtmosStart(false);
    }
}
