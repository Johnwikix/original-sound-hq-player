using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private sealed class CorrectionOutput(string id) : IAudioOutput
    {
        public string DeviceId => id;
        public bool IsFailed => false;
        public int LatencyMs => 10;
        public void Pause() { }
        public void Resume() { }
        public void Dispose() { }
    }

    private static void RunDeviceCorrectionTests()
    {
        var global = new DspSettings { AutoPreamp = true, ConvolutionEnabled = true, Balance = .3,
            NormalizeLoudness = true, HeadroomDb = -3, CurvePoints = "20,1;20000,1" };
        var bindings = new DeviceCorrections { Enabled = true, Bindings = [
            new() { DeviceId = "endpoint-a", DeviceName = "Same name", Settings = new() {
                AutoPreamp = false, ConvolutionSource = ConvolutionSource.Curve, CurvePoints = "20,-6;20000,-6" } },
            new() { DeviceId = "endpoint-b", DeviceName = "Same name", Settings = new() {
                AutoPreamp = false, ConvolutionSource = ConvolutionSource.Wave, ImpulsePath = "speaker.wav" } },
            new() { DeviceId = "asio:driver-guid", DeviceName = "ASIO", Settings = new() {
                AutoPreamp = false, CurvePoints = "20,-9;20000,-9" } }
        ] }.Validate();

        Run("Device correction: stable identity, global controls, unbound bypass and opt-out", () =>
        {
            var a = bindings.Resolve(global, "ENDPOINT-A");
            Require(a.CurvePoints == "20,-6;20000,-6" && a.ConvolutionEnabled, "case insensitive match failed");
            Require(a.Balance == .3 && a.NormalizeLoudness && a.AutoPreamp == true && a.HeadroomDb == -3,
                "device correction overwrote global effects");
            Require(bindings.Resolve(global, "endpoint-b").ImpulsePath == "speaker.wav", "duplicate names confused match");
            Require(!bindings.Resolve(global, null).ConvolutionEnabled && !bindings.Resolve(global, "missing").ConvolutionEnabled,
                "unknown device inherited another correction");
            Require(!(bindings.Resolve(global with { ConvolutionEnabled = false }, "endpoint-a")).ConvolutionEnabled,
                "global convolution switch ignored");
            Require(ReferenceEquals((bindings with { Enabled = false }).Resolve(global, "missing"), global), "legacy mode changed");
        });

        Run("Device correction: atomic mailbox, persistence and invalid snapshot rejection", () =>
        {
            string name = "CorrectionTest-" + Guid.NewGuid().ToString("N");
            using var writer = new DeviceCorrectionMailbox(name);
            using var reader = new DeviceCorrectionMailbox(name);
            writer.Publish(bindings);
            Require(reader.Read().Resolve(global, "endpoint-a").CurvePoints == "20,-6;20000,-6", "snapshot lost");
            try { writer.Publish(bindings with { Bindings = [bindings.Bindings[0], bindings.Bindings[0]] });
                throw new Exception("duplicate identity accepted"); }
            catch (ArgumentException) { }
            Require(reader.Read().Bindings.Length == 3, "invalid publication damaged previous snapshot");
            writer.Publish(bindings with { Bindings = [] });
            Require(!reader.Read().Resolve(global, "endpoint-a").ConvolutionEnabled, "remove did not reach reader");
            var json = System.Text.Json.JsonSerializer.Serialize(bindings, DeviceCorrectionJsonContext.Default.DeviceCorrections);
            var restored = System.Text.Json.JsonSerializer.Deserialize(json, DeviceCorrectionJsonContext.Default.DeviceCorrections)!;
            Require(restored.Enabled && restored.Bindings[2].DeviceId == "asio:driver-guid", "persistence lost ASIO identity");
        });

        Run("Device correction: actual output overrides default selection, fallback and scoped preview", () =>
        {
            var engine = Engine("DirectSound");
            Set(engine, "_streamLock", new object());
            engine.UpdateDsp(global);
            engine.UpdateDeviceCorrections(bindings);
            DspSettings Resolve(string? id) => (DspSettings)Invoke(engine, "ResolveDsp", id)!;
            Set(engine, "_output", new CorrectionOutput("endpoint-a"));
            Require(engine.GetDspState().OutputDeviceId == "endpoint-a", "default output did not report actual identity");
            Require(Resolve("endpoint-a").CurvePoints == "20,-6;20000,-6", "default output match failed");
            Set(engine, "_output", new CorrectionOutput("endpoint-b"));
            Require(Resolve("endpoint-b").ImpulsePath == "speaker.wav", "default device switch did not select B");
            engine.OutputMode = "ASIO";
            Require(Resolve("asio:driver-guid").CurvePoints == "20,-9;20000,-9", "ASIO binding failed");
            Require(Resolve("endpoint-b").ImpulsePath == "speaker.wav", "ASIO fallback used requested driver correction");
            engine.UpdateDsp(global with { ConvolutionEnabled = false, HeadroomDb = -35 }, preview: true);
            Require(!Resolve("endpoint-b").ConvolutionEnabled, "audition bypass did not apply to the audition device");
            Require(Resolve("endpoint-a").ConvolutionEnabled && Resolve("endpoint-a").HeadroomDb == global.HeadroomDb,
                "audition bypass or gain leaked into the next physical output");
            engine.UpdateDsp(global, preview: true);
            Require(Resolve("endpoint-b").CurvePoints == global.CurvePoints, "preview did not override binding");
            Require(Resolve("endpoint-a").CurvePoints == "20,-6;20000,-6", "preview leaked to another device");
            Set(engine, "_output", new CorrectionOutput("endpoint-a"));
            engine.UpdateDsp(global, preview: true);
            Require(Resolve("endpoint-a").CurvePoints == "20,-6;20000,-6", "subsequent preview followed new device");
            engine.UpdateDsp(global);
            Require(Resolve("endpoint-b").ImpulsePath == "speaker.wav", "preview cancellation failed to restore binding");
            engine.UpdateDeviceCorrections(bindings with { Bindings = [] });
            Require(!Resolve("endpoint-b").ConvolutionEnabled, "unbinding left correction active");
        });

        Run("Device correction: old filter cannot leak into a newly opened output", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            var a = global with { NormalizeLoudness = false, AutoPreamp = false, HeadroomDb = 0,
                ConvolutionSource = ConvolutionSource.Curve, CurvePoints = "20,-12;20000,-12" };
            effects.Configure(a);
            Require(SpinWait.SpinUntil(() => effects.GetState(0, false).Convolution == ConvolutionStatus.Active, 5000), "curve load timeout");
            var samples = new double[4096]; samples.AsSpan().Fill(1);
            effects.ApplyInput(samples, 2048); effects.ApplyConvolution(samples, 2048);
            effects.ResetForOutput();
            effects.Configure(a with { ConvolutionEnabled = false });
            samples.AsSpan().Fill(1);
            effects.ApplyInput(samples, 2048); effects.ApplyConvolution(samples, 2048);
            Require(samples.All(x => x == 1), "old correction or tail leaked into unbound device");
        });

        Run("Device correction: output identity state roundtrip and legacy state decoding", () =>
        {
            var state = new DspState(0, false, 2, LoudnessStatus.Off, 0, 0, OutputDeviceId: "endpoint-a");
            byte[] data = new byte[DspProtocol.StateSize];
            DspProtocol.WriteState(data, state);
            Require(DspProtocol.ReadState(data) == state, "identity lost over IPC");
            byte[] old = data[..30]; old[3] = 4;
            Require(DspProtocol.ReadState(old).OutputDeviceId == "", "old state must not invent an identity");
            data[30] = 1; data[31] = 1;
            try { DspProtocol.ReadState(data); throw new Exception("oversized device identity accepted"); }
            catch (ArgumentException) { }
            using var mailbox = new DspStateMailbox(true, "CorrectionState-" + Guid.NewGuid().ToString("N"));
            mailbox.Publish(state);
            Require(mailbox.Publish(state with { OutputDeviceId = "endpoint-b" }), "device-only change was not published");
            Require(mailbox.Read()!.State.OutputDeviceId == "endpoint-b", "new output state lost");
        });
    }
}
