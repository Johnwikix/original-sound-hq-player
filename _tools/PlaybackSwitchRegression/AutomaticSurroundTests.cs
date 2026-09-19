using System.Runtime.CompilerServices;
using AudioPlayer.Interop;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunAutomaticSurroundTests(string root)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "automatic-surround.wav");
        WriteSurroundWave(path, 0x60F);
        try
        {
            Run("Auto 5.1: temporary exclusive keeps PCM volume, including mute", () =>
            {
                var engine = Engine("WasapiShared");
                Set(engine, "_streamLock", new object());
                engine.ExperimentalSurround51 = true;
                engine.Volume = .25f;
                using var session = (Session)Invoke(engine, "OpenSession", path, false, null)!;
                Set(engine, "_session", session);
                using var output = new WasapiOutput(true, false);
                Set(engine, "_output", output);
                Require(session.Channels == 6 && session.Gain!.Target == .25, "exclusive route starts at full scale");
                engine.SetVolume(0);
                Require(session.Gain.Target == 0, "mute was sent to a nonexistent shared volume interface");
                engine.SetVolume(.4);
                Require(Math.Abs(session.Gain.Target - .4) < 1e-6 && engine.OutputMode == "WasapiShared", "volume or saved mode changed");
            });
            Run("Auto 5.1: fallback downmixes once, preserves seek and does not reattempt exclusive", () =>
            {
                var engine = Engine("WasapiShared");
                Set(engine, "_streamLock", new object());
                engine.MusicUrl = path;
                engine.ExperimentalSurround51 = true;
                using var session = (Session)Invoke(engine, "OpenSession", path, false, null)!;
                session.AnchorFrames = session.MsToFrames(400);
                Set(engine, "_session", session);
                try
                {
                    Require((bool)Invoke(engine, "RebuildSurroundFallback", session)!, "stereo fallback failed");
                    var fallback = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
                    Require(fallback.Channels == 2 && fallback.ChannelMask == 0 && fallback.Gain!.Target == 1
                        && fallback.CurrentMs == 400, "fallback lost position or double-applied shared volume");
                    Invoke(engine, "CompleteSurroundStart", true);
                    Invoke(engine, "CompleteSurroundStart", true);
                    Require(engine.GetDspState().SurroundFailureSequence == 1, "duplicate failure");
                    using var retry = (Session)Invoke(engine, "OpenSession", path, false, null)!;
                    Require(retry.Channels == 2, "automatic retry reacquired exclusive");
                    engine.ChangeWaveChannelTime(250);
                    Require(engine.GetTimeProgress().currentMs == 250, "seek after fallback failed");
                }
                finally { Invoke(engine, "DisposeSession"); }
            });
            Run("Auto 5.1: paused setting toggles retain position and ordinary output preference", () =>
            {
                var engine = Engine("WasapiShared");
                Set(engine, "_streamLock", new object());
                engine.MusicUrl = path;
                using var session = (Session)Invoke(engine, "OpenSession", path, false, null)!;
                session.AnchorFrames = session.MsToFrames(300);
                Set(engine, "_session", session);
                var settings = new IpcSetting { OutputMode = "WasapiShared", BassOutputDeviceId = -1,
                    Latency = 300, DsdPcmFreq = 88200, ExperimentalSurround51 = true, Volume = .2f };
                try
                {
                    engine.UpdateSettings(settings);
                    Require(!engine.IsPlaying && engine.GetDspState().Channels == 6 && engine.GetTimeProgress().currentMs == 300,
                        "enabling while paused changed play intent/position");
                    settings.ExperimentalSurround51 = false;
                    engine.UpdateSettings(settings);
                    Require(!engine.IsPlaying && engine.GetDspState().Channels == 2 && engine.OutputMode == "WasapiShared",
                        "disabling failed to restore ordinary output");
                }
                finally { Invoke(engine, "DisposeSession"); }
            });
            Run("Auto 5.1: unsupported channel layouts retain normal shared downmix", () =>
            {
                string other = Path.Combine(AppContext.BaseDirectory, "automatic-surround-71.wav");
                WriteSurroundWave(other, 0x63F, 8);
                try
                {
                    var engine = Engine("WasapiShared");
                    engine.ExperimentalSurround51 = true;
                    using var session = (Session)Invoke(engine, "OpenSession", other, false, null)!;
                    Require(session.Channels == 2 && session.ChannelMask == 0, "7.1 silently escaped into automatic 5.1");
                    using var stereo = (Session)Invoke(engine, "OpenSession", Path.Combine(root, "_tools", "test_tone.wav"), false, null)!;
                    Require(stereo.Channels == 2 && stereo.ChannelMask == 0, "stereo became surround");
                }
                finally { File.Delete(other); }
            });
            Run("Auto 5.1: missing Windows endpoint stops without migrating to default speakers", () =>
            {
                var engine = Engine("WasapiShared");
                Set(engine, "_streamLock", new object());
                Set(engine, "_ipc", RuntimeHelpers.GetUninitializedObject(typeof(AudioPlayer.PlayerIpcService)));
                engine.ExperimentalSurround51 = true;
                engine.WasapiEndpointId = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";
                try
                {
                    engine.PlayMusic(path);
                    var state = engine.GetDspState();
                    Require(!engine.IsPlaying && state.SurroundStatus == SurroundPlaybackStatus.Stopped
                        && state.SurroundFailureStopped && state.SurroundFailureSequence == 1,
                        "missing endpoint did not report stopped outcome");
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
            Run("Auto 5.1: IPC status round-trip and previous v7 state remain compatible", () =>
            {
                var state = new DspState(0, false, 2, LoudnessStatus.Off, 0, double.NaN,
                    SurroundStatus: SurroundPlaybackStatus.Fallback, SurroundFailureSequence: 42);
                byte[] data = new byte[DspProtocol.StateSize];
                DspProtocol.WriteState(data, state);
                Require(DspProtocol.ReadState(data) == state, "surround failure state lost");
                data[3] = 7;
                Require(DspProtocol.ReadState(data.AsSpan(0, 309)).SurroundStatus == SurroundPlaybackStatus.Off,
                    "v7 state no longer readable");
            });
            Run("Auto 5.1: ASIO failure downmixes without selecting a Windows device", () =>
            {
                var engine = Engine("ASIO");
                engine.ExperimentalSurround51 = true;
                engine.MusicUrl = path;
                engine.Volume = .3f;
                using var session = (Session)Invoke(engine, "OpenSession", path, false, null)!;
                Set(engine, "_session", session);
                try
                {
                    Require((bool)Invoke(engine, "RebuildSurroundFallback", session)!, "ASIO fallback failed");
                    var fallback = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
                    Require(engine.OutputMode == "ASIO" && fallback.Channels == 2
                        && Math.Abs(fallback.Gain!.Target - .3) < 1e-6, "ASIO fallback changed mode or gain");
                }
                finally { Invoke(engine, "DisposeSession"); }
            });
            Run("Auto 5.1: reuse compatible exclusive output, but restore shared for stereo and honor default changes", () =>
            {
                var engine = Engine("WasapiShared");
                engine.ExperimentalSurround51 = true;
                using var native = new NativeObject(16);
                using var output = new WasapiOutput(true, false);
                Set(output, "_client", new RawAudioClient(native.Pointer));
                Set(output, "_source", new SurroundSource(0x60F));
                Set(output, "<DeviceId>k__BackingField", "receiver");
                Set(engine, "_output", output);
                using var next = (Session)Invoke(engine, "OpenSession", path, false, null)!;
                using var stereo = (Session)Invoke(engine, "OpenSession", Path.Combine(root, "_tools", "test_tone.wav"), false, null)!;
                try
                {
                    Require((bool)Invoke(engine, "TryReuseExclusiveOutput", next)!, "consecutive 5.1 tracks unnecessarily reopened the device");
                    Require(!(bool)Invoke(engine, "TryReuseExclusiveOutput", stereo)!, "ordinary stereo retained temporary exclusive");
                    Set(engine, "_lastDefaultDeviceId", "other-speaker");
                    Require(!(bool)Invoke(engine, "TryReuseExclusiveOutput", next)!, "default-device change reused the wrong receiver");
                }
                finally { Set(output, "_client", null); }
            });
            Run("Auto 5.1: Atmos takes priority and its PCM fallback does not reacquire surround exclusive", () =>
            {
                var engine = Engine("WasapiShared");
                engine.ExperimentalAtmosPassthrough = true;
                engine.ExperimentalSurround51 = true;
                string atmos = Path.Combine(AppContext.BaseDirectory, "Fixtures", "eac3-5.1.m4a");
                using var encoded = (Session)Invoke(engine, "OpenSession", atmos, false, null)!;
                Require(encoded.Kind == RenderKind.Eac3, "surround stole Atmos priority");
                Set(engine, "_atmosUseSharedPcm", true);
                using var fallback = (Session)Invoke(engine, "OpenSession", atmos, false, RenderKind.Pcm)!;
                Require(fallback.Channels == 2 && fallback.ChannelMask == 0, "Atmos fallback retried exclusive via 5.1");
            });
        }
        finally { File.Delete(path); }
    }
}
