using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunSpectralSessionTests(string root)
    {
        foreach (string mode in new[] { "ASIO", "WasapiExclusivePush", "WasapiExclusiveEvent" })
        {
            Run($"Enhancement {mode}: PCM negotiates 2x, seeks and leaves bitstream intact", () =>
            {
                var engine = Engine(mode);
                engine.SpectralEnhancement = SpectralEnhancementMode.LossyAndLowRateLossless;
                string path = Path.Combine(root, "_tools", "test_tone.wav");
                using var pcm = (Session?)Invoke(engine, "OpenSession", path, false, null);
                Require(pcm is { Kind: RenderKind.Pcm, EnhancementActive: true, SampleRate: 88200 }, "Enhanced session did not negotiate 2x");
                CheckProgress(pcm!);
                pcm!.RequestSeek(1000, 77);
                Require(SpinWait.SpinUntil(() => Volatile.Read(ref pcm.CompletedSeekId) == 77, 3000), "Enhanced seek did not complete");
                Require(pcm.DecodeFailure == null, "Enhanced decode failed after seeking");
                Set(engine,"_dspSettings",new DspSettings {IsEnabled=false});
                using var bypass=(Session?)Invoke(engine,"OpenSession",path,false,null);
                Require(bypass is {EnhancementActive:false,SampleRate:44100},"DSP off still upsamples");
                Require(engine.SpectralEnhancement==SpectralEnhancementMode.LossyAndLowRateLossless,"DSP off erased scope preference");
                Set(engine,"_dspSettings",new DspSettings {IsEnabled=true});
                using var restored=(Session?)Invoke(engine,"OpenSession",path,false,null);
                Require(restored is {EnhancementActive:true,SampleRate:88200},"DSP on did not restore upsampling");
                using var bitstream = (Session?)Invoke(engine, "OpenSession", Path.Combine(AppContext.BaseDirectory, "switch-test.dsf"), false, null);
                Require(bitstream != null && bitstream.Kind == (mode == "ASIO" ? RenderKind.NativeDsd : RenderKind.Dop)
                    && !bitstream.EnhancementActive, "Enhancement entered a DSD bitstream");
                CheckProgress(bitstream!);
            });
        }
    }
}
