using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPlayer.Interop;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static int _formatQueryThread;
    private static int _formatQueryResult;

    private static void RunAtmosAutomaticTests(string root)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "eac3-5.1.m4a");
        Run("Auto Atmos: ASIO requires explicit HDMI without changing ordinary output", () =>
        {
            var engine = Engine("ASIO");
            engine.ExperimentalAtmosPassthrough = true;
            using var ordinary = (Session?)Invoke(engine, "OpenSession", path, false, null);
            Require(ordinary?.Kind == RenderKind.Pcm && engine.OutputMode == "ASIO", "ASIO was silently redirected");
            engine.AtmosEndpointId = "explicit-hdmi";
            Invoke(engine, "ResetAtmosAttempt");
            using var encoded = (Session?)Invoke(engine, "OpenSession", path, false, null);
            Require(encoded?.Kind == RenderKind.Eac3 && engine.OutputMode == "ASIO", "explicit HDMI did not enable temporary exclusive");
        });
        Run("Auto Atmos: pause/settings/seek preserve intent and original shared preference", () =>
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            engine.MusicUrl = path;
            using var initial = (Session?)Invoke(engine, "OpenSession", path, false, null);
            initial!.AnchorFrames = initial.MsToFrames(500);
            Set(engine, "_session", initial);
            var settings = new IpcSetting { OutputMode = "WasapiShared", BassOutputDeviceId = -1,
                WasapiEndpointId = "saved-device", ExperimentalAtmosPassthrough = true,
                Volume = .3f, Latency = 300, DsdPcmFreq = 88200 };
            try
            {
                engine.UpdateSettings(settings);
                var prepared = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
                Require(!engine.IsPlaying && prepared.Kind == RenderKind.Eac3 && prepared.CurrentMs == 500,
                    "enabling while paused started playback or lost position");
                Require(engine.OutputMode == "WasapiShared" && engine.WasapiEndpointId == "saved-device", "preferences overwritten");
                engine.ChangeWaveChannelTime(250);
                settings.ExperimentalAtmosPassthrough = false;
                engine.UpdateSettings(settings);
                var restored = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
                Require(!engine.IsPlaying && restored.Kind == RenderKind.Pcm && restored.CurrentMs == 250,
                    "disabling failed to restore PCM and paused seek position");
                Require(restored.Gain != null && engine.Volume == .3f, "PCM gain/volume preference lost");
            }
            finally { Invoke(engine, "DisposeSession"); }
        });
        Run("Auto Atmos: fallback stays PCM through seek/recovery and records one failure", () =>
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            engine.MusicUrl = path;
            engine.ExperimentalAtmosPassthrough = true;
            using var encoded = (Session?)Invoke(engine, "OpenSession", path, false, null);
            encoded!.AnchorFrames = encoded.MsToFrames(400);
            Set(engine, "_session", encoded);
            Set(engine, "_atmosResolvedEndpoint", "pinned-hdmi");
            Set(engine, "_atmosReason", AtmosFailure.DeviceBusy);
            try
            {
                Require((bool)Invoke(engine, "RebuildAtmosFallback", encoded)!, "fallback failed");
                Invoke(engine, "CompleteAtmosStart", true);
                Invoke(engine, "CompleteAtmosStart", true);
                var state = engine.GetDspState();
                Require(state.AtmosStatus == AtmosPlaybackStatus.PcmFallback && state.AtmosFailureSequence == 1
                    && state.LastAtmosFailure == AtmosFailure.DeviceBusy && !state.AtmosFailureStopped, "incorrect/duplicate failure state");
                using var retry = (Session?)Invoke(engine, "OpenSession", path, false, RenderKind.Eac3);
                Require(retry?.Kind == RenderKind.Pcm, "recovery reacquired exclusive mode");
                engine.ChangeWaveChannelTime(300);
                Require(engine.GetTimeProgress().currentMs == 300, "fallback seek lost position");
                Invoke(engine, "ResetAtmosAttempt");
                using var next = (Session?)Invoke(engine, "OpenSession", path, false, null);
                Require(next?.Kind == RenderKind.Eac3, "next explicit attempt stayed disabled");
                Require(engine.GetDspState().AtmosFailureSequence == 1, "failure evidence lost before IPC could read it");
            }
            finally { Invoke(engine, "DisposeSession"); }
        });
        Run("Auto Atmos: unresolved fallback never opens default output", () =>
        {
            var engine = Engine("DirectSound");
            Set(engine, "_atmosUseSharedPcm", true);
            using var pcm = Source(engine, RenderKind.Pcm);
            Require(Invoke(engine, "CreateSharedOutput", pcm, -1) == null, "missing pinned endpoint opened default speaker");
        });
        Run("Auto Atmos: missing real endpoint stops safely and publishes failure through IPC mailbox", () =>
        {
            string name = "AtmosMissingEndpoint-" + Guid.NewGuid().ToString("N");
            using var writer = new DspStateMailbox(true, name);
            using var reader = new DspStateMailbox(false, name);
            var ipc = (AudioPlayer.PlayerIpcService)RuntimeHelpers.GetUninitializedObject(typeof(AudioPlayer.PlayerIpcService));
            Set(ipc, "_dspMailbox", writer);
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            Set(engine, "_ipc", ipc);
            engine.AtmosEndpointId = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";
            engine.ExperimentalAtmosPassthrough = true;
            try
            {
                engine.PlayMusic(path); // Real MMDevice resolution; this ID cannot open physical output.
                Require(!engine.IsPlaying && engine.OutputMode == "WasapiShared", "missing endpoint migrated to default output");
                Require(reader.Changed.WaitOne(3000), "failure publication was lost");
                var state = reader.Read()!.State;
                Require(state.AtmosStatus == AtmosPlaybackStatus.Stopped && state.AtmosFailureSequence == 1
                    && state.LastAtmosFailure == AtmosFailure.DeviceUnavailable && state.AtmosFailureStopped
                    && state.ActualOutput == ActualOutputMode.None, "IPC did not report actual failure outcome");
            }
            finally
            {
                // Drain publication under the same lock before releasing the mailbox.
                lock (typeof(PlaybackEngine).GetField("_streamLock", Private)!.GetValue(engine)!)
                {
                    Set(engine, "_disposed", 1);
                    Invoke(engine, "DisposeSession");
                }
            }
        });
        Run("Auto Atmos: device loss pauses at the audible position and emits a distinct stopped outcome", () =>
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            Set(engine, "_ipc", RuntimeHelpers.GetUninitializedObject(typeof(AudioPlayer.PlayerIpcService)));
            engine.ExperimentalAtmosPassthrough = true;
            engine.MusicUrl = path;
            using var session = (Session?)Invoke(engine, "OpenSession", path, false, null);
            session!.AnchorFrames = session.MsToFrames(600);
            Set(engine, "_session", session);
            Set(engine, "_atmosResolvedEndpoint", "receiver");
            Set(engine, "_atmosReason", AtmosFailure.DeviceBusy);
            // This represents a previous fallback; later disconnection is a new outcome.
            Invoke(engine, "CompleteAtmosStart", true);
            engine.IsPlaying = true;
            try
            {
                lock (typeof(PlaybackEngine).GetField("_streamLock", Private)!.GetValue(engine)!)
                    Invoke(engine, "StopAtmosForDeviceChange");
                var state = engine.GetDspState();
                Require(!engine.IsPlaying && engine.GetTimeProgress().currentMs == 600, "device loss discarded position/play intent");
                Require(state.AtmosFailureSequence == 2 && state.AtmosFailureStopped
                    && state.LastAtmosFailure == AtmosFailure.DeviceUnavailable, "stop after PCM fallback was suppressed");
            }
            finally
            {
                lock (typeof(PlaybackEngine).GetField("_streamLock", Private)!.GetValue(engine)!)
                {
                    Set(engine, "_disposed", 1);
                    Invoke(engine, "DisposeSession");
                }
            }
        });
        Run("Auto Atmos: versioned state and optional device survive IPC", () =>
        {
            var state = new DspState(0, false, 2, LoudnessStatus.Off, 0, double.NaN,
                AtmosStatus: AtmosPlaybackStatus.PcmFallback, AtmosReason: AtmosFailure.DeviceBusy,
                AtmosFailureSequence: 14, LastAtmosFailure: AtmosFailure.DeviceBusy, ActualOutput: ActualOutputMode.Shared);
            byte[] data = new byte[DspProtocol.StateSize];
            DspProtocol.WriteState(data, state);
            Require(DspProtocol.ReadState(data) == state, "Atmos state was lost");
            data[3] = 6;
            Require(DspProtocol.ReadState(data.AsSpan(0, 296)).AtmosFailureSequence == 0, "v6 state no longer readable");
            byte[] settings = new byte[BinarySerializer.IpcSettingSize];
            int n = BinarySerializer.WriteIpcSetting(settings, new() { AtmosEndpointId = "hdmi-id", ExperimentalAtmosPassthrough = true });
            Require(BinarySerializer.ReadIpcSetting(settings.AsSpan(0, n)).AtmosEndpointId == "hdmi-id", "HDMI preference lost");
            Require(BinarySerializer.ReadIpcSetting(settings.AsSpan(0, n - 9)).ExperimentalAtmosPassthrough, "previous Atmos setting no longer readable");
        });
        Run("Auto Atmos: capability rejection runs off control thread and skips Initialize", () =>
        {
            using var native = new NativeObject(16);
            native.Table[7] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int, WAVEFORMATEX*, WAVEFORMATEX**, int>)&RejectIecFormat;
            using var output = new WasapiOutput(true, false);
            Set(output, "_client", new RawAudioClient(native.Pointer));
            _formatQueryThread = 0;
            _formatQueryResult = WasapiTypes.AudclntEUnsupportedFormat;
            try
            {
                var format = WAVEFORMATEXTENSIBLE_IEC61937.Eac3(0x60F, true);
                int hr = (int)Invoke(output, "InitializeWithTimeout", 1, 0, 320000L, 320000L,
                    System.Reflection.Pointer.Box(&format, typeof(WAVEFORMATEXTENSIBLE*)))!;
                Require(hr == _formatQueryResult && _formatQueryThread != Environment.CurrentManagedThreadId,
                    "capability query was inline or rejection ignored");
                Require(_capturedIecFormat.EncodedChannelCount == 6 && _capturedIecFormat.FormatExt.SubFormat == format.FormatExt.SubFormat,
                    "capability query lost IEC extension");
            }
            finally { Set(output, "_client", null); }
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectIecFormat(IntPtr self, int mode, WAVEFORMATEX* format, WAVEFORMATEX** closest)
    {
        _formatQueryThread = Environment.CurrentManagedThreadId;
        _capturedIecFormat = *(WAVEFORMATEXTENSIBLE_IEC61937*)format;
        return _formatQueryResult;
    }
}
