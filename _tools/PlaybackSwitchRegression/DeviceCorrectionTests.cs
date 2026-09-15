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
                ConvolutionSource = ConvolutionSource.Curve, CurvePoints = "20,-6;20000,-6" } },
            new() { DeviceId = "endpoint-b", DeviceName = "Same name", Settings = new() {
                ConvolutionSource = ConvolutionSource.Wave, ImpulsePath = "speaker.wav" } },
            new() { DeviceId = "asio:driver-guid", DeviceName = "ASIO", Settings = new() {
                CurvePoints = "20,-9;20000,-9" } }
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
            engine.PreviewDsp(new(1, 0, "endpoint-b", false, global with { ConvolutionEnabled = false, HeadroomDb = -35 }));
            Require(!Resolve("endpoint-b").ConvolutionEnabled, "audition bypass did not apply to the audition device");
            Require(Resolve("endpoint-a").ConvolutionEnabled && Resolve("endpoint-a").HeadroomDb == global.HeadroomDb,
                "audition bypass or gain leaked into the next physical output");
            engine.PreviewDsp(new(2, 0, "endpoint-b", false, global));
            Require(Resolve("endpoint-b").CurvePoints == global.CurvePoints, "preview did not override binding");
            Require(Resolve("endpoint-a").CurvePoints == "20,-6;20000,-6", "preview leaked to another device");
            Set(engine, "_output", new CorrectionOutput("endpoint-a"));
            engine.PreviewDsp(new(3, 0, "endpoint-b", false, global));
            Require(Resolve("endpoint-a").CurvePoints == "20,-6;20000,-6", "subsequent preview followed new device");
            engine.UpdateDsp(global);
            Require(Resolve("endpoint-b").ImpulsePath == "speaker.wav", "preview cancellation failed to restore binding");
            engine.UpdateDeviceCorrections(bindings with { Bindings = [] });
            Require(!Resolve("endpoint-b").ConvolutionEnabled, "unbinding left correction active");
        });

        Run("Device correction: late preview, A-B-A generation and closed preview rejection", () =>
        {
            var engine = Engine("DirectSound");
            Set(engine, "_streamLock", new object());
            engine.UpdateDsp(global);
            engine.UpdateDeviceCorrections(bindings);
            Set(engine, "_output", new CorrectionOutput("endpoint-a"));
            Set(engine, "_outputGeneration", 3L);
            DspSettings Resolve() => (DspSettings)Invoke(engine, "ResolveDsp", "endpoint-a")!;
            engine.PreviewDsp(new(1, 1, "endpoint-a", false, global));
            Require(Resolve().CurvePoints == bindings.Bindings[0].Settings.CurvePoints, "A-B-A accepted old A generation");
            engine.PreviewDsp(new(2, 3, "endpoint-b", false, global));
            Require(Resolve().CurvePoints != global.CurvePoints, "late B preview applied to A");
            var preview = new DspPreview(3, 3, "endpoint-a", false, global);
            byte[] bytes = new byte[DspPreview.Size]; preview.Write(bytes);
            engine.PreviewDsp(DspPreview.Read(bytes));
            Require(Resolve().CurvePoints == global.CurvePoints, "current target preview rejected");
            engine.PreviewDsp(new(5, 0, "", true, global));
            engine.PreviewDsp(preview with { Sequence = 4 });
            Require(Resolve().CurvePoints != global.CurvePoints, "closed preview resurrected");
        });

        Run("Device correction: atomic migration, backup recovery, write failure and invalid schema", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "correction-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "AudioCorrections.json");
            try
            {
                var store = new AudioCorrectionStore(path);
                var migrated = store.LoadAsync(bindings).GetAwaiter().GetResult();
                Require(migrated.Bindings.Length == 3, "legacy migration lost bindings");
                var json = File.ReadAllText(path);
                Require(!json.Contains("NormalizeLoudness") && !json.Contains("HeadroomDb"), "device file contains global DSP preferences");
                File.WriteAllText(path + ".bak", json);
                var second = new AudioCorrectionStore(path);
                Require(second.LoadAsync(new()).GetAwaiter().GetResult().Bindings.Length == 3, "migration ran twice");
                Require(!File.Exists(path + ".bak"), "valid main file did not retire legacy backup");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { store.SaveAsync(bindings with { Enabled = false }).GetAwaiter().GetResult(); throw new Exception("locked write succeeded"); }
                    catch (IOException) { }
                }
                Require(File.ReadAllText(path) == json, "failed write changed committed file");
                store.SaveAsync(bindings with { Enabled = false }).GetAwaiter().GetResult();
                Require(!File.Exists(path + ".bak"), "correction save created an unwanted backup");
                // Simulate a backup produced by the previous app version.
                File.WriteAllText(path + ".bak", json);
                File.WriteAllText(path, "{broken");
                var recovery = new AudioCorrectionStore(path);
                Require(recovery.LoadAsync(new()).GetAwaiter().GetResult().Enabled, "valid backup was not recovered");
                Require(!File.Exists(path + ".bak"), "legacy backup not removed after recovery");
                recovery.SaveAsync(bindings).GetAwaiter().GetResult();
                Require(new AudioCorrectionStore(path).LoadAsync(new()).GetAwaiter().GetResult().Bindings.Length == 3, "recovery save failed");
                File.WriteAllText(path, "{\"SchemaVersion\":2,\"Revision\":1,\"Corrections\":{}}");
                try { new AudioCorrectionStore(path).LoadAsync(new()).GetAwaiter().GetResult(); throw new Exception("unknown schema overwritten"); }
                catch (NotSupportedException) { }
                File.WriteAllText(path, "{}"); File.WriteAllText(path + ".bak", "{}");
                var invalid = new AudioCorrectionStore(path);
                try { invalid.LoadAsync(bindings).GetAwaiter().GetResult(); throw new Exception("corrupt files remigrated"); }
                catch (System.Text.Json.JsonException) { }
                try { invalid.SaveAsync(bindings).GetAwaiter().GetResult(); throw new Exception("failed load permitted save"); }
                catch (InvalidOperationException) { }
            }
            finally
            {
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        });

        Run("Device correction: prepared coefficients reuse without sharing render history", () =>
        {
            var settings = global with { ConvolutionSource = ConvolutionSource.Curve };
            var first = PreparedCorrectionCache.Get(settings, 48000, 2, default);
            var second = PreparedCorrectionCache.Get(settings, 48000, 2, default);
            Require(ReferenceEquals(first, second), "same curve was prepared twice");
            var a = new ConvolutionFilter(first.Coefficients);
            var b = new ConvolutionFilter(second.Coefficients);
            double mix = 1, gain = 1;
            var samples = new double[1024]; samples[0] = samples[1] = 1;
            a.Process(samples, 512, 1, ref mix, ref gain, 1);
            Array.Clear(samples);
            b.Process(samples, 512, 1, ref mix, ref gain, 1);
            Require(samples.All(x => x == 0), "coefficient reuse shared history");
        });

        Run("Device correction: old filter cannot leak into a newly opened output", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            var a = global with { NormalizeLoudness = false, AutoPreamp = false, HeadroomDb = 0,
                ConvolutionSource = ConvolutionSource.Curve, CurvePoints = "20,-12;20000,-12" };
            effects.Configure(a);
            Require(SpinWait.SpinUntil(() => effects.GetState(0, false).Convolution == ConvolutionStatus.Active, 5000), "curve load timeout");
            var filterField = typeof(PcmEffects).GetField("_filter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var preparedFilter = filterField.GetValue(effects);
            effects.ResetForOutput(a);
            effects.Configure(a);
            Require(ReferenceEquals(preparedFilter, filterField.GetValue(effects)), "same-profile output reset rebuilt the filter");
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
            var state = new DspState(0, false, 2, LoudnessStatus.Off, 0, 0, OutputDeviceId: "endpoint-a", OutputGeneration: 77);
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
