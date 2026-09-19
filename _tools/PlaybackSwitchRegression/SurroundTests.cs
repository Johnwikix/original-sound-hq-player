using System.Reflection;
using AudioPlayer.Decode;
using AudioPlayer.Interop;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunSurroundTests(string root)
    {
        Run("5.1: settings append is optional and defaults off for old IPC", () =>
        {
            byte[] bytes = new byte[BinarySerializer.IpcSettingSize];
            var settings = new IpcSetting { OutputMode = "ASIO", WasapiEndpointId = "endpoint", ExperimentalSurround51 = true };
            int length = BinarySerializer.WriteIpcSetting(bytes, settings);
            Require(BinarySerializer.ReadIpcSetting(bytes.AsSpan(0, length)).ExperimentalSurround51, "flag lost");
            Require(!BinarySerializer.ReadIpcSetting(bytes.AsSpan(0, length - 4)).ExperimentalSurround51, "old payload enabled experiment");
            Require(BinarySerializer.ReadIpcSetting(bytes.AsSpan(0, length - 4)).WasapiEndpointId == "endpoint", "old endpoint lost");
        });
        foreach (uint mask in new uint[] { 0x3F, 0x60F })
        {
            string path = Path.Combine(AppContext.BaseDirectory, $"surround-{mask:X}.wav");
            WriteSurroundWave(path, mask);
            foreach (bool enabled in new[] { false, true })
            {
                Run($"5.1 {mask:X} enabled={enabled}: decode preserves all six channel samples", () =>
                {
                    using var decoder = new PcmDecoder();
                    Require(decoder.Open(path, 88200, 0, experimentalSurround51: enabled), "decoder failed");
                    Require(decoder.ChannelMask == (enabled ? mask : 0), "incorrect opt-in mask");
                    double[] pcm = new double[1024 * 6];
                    int frames = decoder.Read(pcm);
                    Require(frames > 0 && decoder.Channels == 6, "no six-channel PCM");
                    for (int f = 0; f < frames; f++)
                        for (int ch = 0; ch < 6; ch++)
                            Require(Math.Abs(pcm[f * 6 + ch] - (ch + 1) * 1000 / 32768.0) < 1e-12, "channel reordered or lost");
                });
                foreach (string mode in new[] { "ASIO", "WasapiExclusivePush", "WasapiExclusiveEvent", "WasapiShared", "DirectSound" })
                    Run($"5.1 {mask:X} enabled={enabled}: {mode} scope", () =>
                    {
                        var engine = Engine(mode);
                        engine.ExperimentalSurround51 = enabled;
                        using var session = (Session?)Invoke(engine, "OpenSession", path, false, null);
                        bool shared = mode is "WasapiShared" or "DirectSound";
                        Require(session != null && session.Channels == (shared && !enabled ? 2 : 6), "existing channel policy changed");
                        Require(session!.ChannelMask == (enabled ? mask : 0), "experiment escaped output scope");
                        CheckProgress(session);
                    });
            }
            Run($"5.1 {mask:X}: every WASAPI PCM format carries correct mask and stride", () =>
            {
                for (int kind = 0; kind <= 4; kind++)
                {
                    var format = WasapiOutput.MakeFormat(new SurroundSource(mask), kind);
                    Require(format.dwChannelMask == mask && format.Format.nChannels == 6, "wrong endpoint speaker mask");
                    Require(format.Format.nBlockAlign == 6 * format.Format.wBitsPerSample / 8, "incorrect frame stride");
                    Require(WasapiOutput.MakeFormat(new SurroundSource(0), kind).dwChannelMask == 0, "legacy mask changed");
                }
            });
            File.Delete(path);
        }
        Run("5.1: stereo and DSD opt-in produce exactly the old PCM", () =>
        {
            foreach (string path in new[] { Path.Combine(root, "_tools", "test_tone.wav"), Path.Combine(AppContext.BaseDirectory, "switch-test.dsf") })
            {
                using var old = new PcmDecoder();
                using var experimental = new PcmDecoder();
                Require(old.Open(path, 88200, 0) && experimental.Open(path, 88200, 0, experimentalSurround51: true), "open failed");
                double[] a = new double[8192], b = new double[8192];
                int n = old.Read(a), m = experimental.Read(b);
                Require(n == m && a.AsSpan().SequenceEqual(b) && experimental.ChannelMask == 0, "stereo/DSD samples changed");
            }
        });
        Run("5.1: ASIO refuses partial outputs only with experimental mask", () =>
        {
            foreach (int available in new[] { 2, 4, 6, 8 })
            {
                Require(AsioOutput.SelectOutputChannelCount(new SurroundSource(0x60F), available) == (available < 6 ? 0 : 6), "partial experimental output accepted");
                Require(AsioOutput.SelectOutputChannelCount(new SurroundSource(0), available) == Math.Min(6, available), "legacy ASIO policy changed");
            }
        });
        Run("5.1: ASIO writes six distinct channels in documented order", () =>
        {
            using var asio = new AsioOutput();
            double[] samples = Enumerable.Range(0, 18).Select(i => (i % 6 + 1) / 16.0).ToArray();
            Set(asio, "_pcmScratch", samples);
            double* output = stackalloc double[3];
            for (int ch = 0; ch < 6; ch++)
            {
                Invoke(asio, "WritePcmChannel", (IntPtr)output, AsioConstants.AsioStFloat64Lsb, ch, 3, 6);
                for (int f = 0; f < 3; f++) Require(output[f] == samples[f * 6 + ch], "ASIO channel data mismatch");
            }
        });
        foreach (bool asioMode in new[] { false, true })
            Run($"5.1: {(asioMode ? "ASIO" : "WASAPI")} cannot reuse a different speaker layout", () =>
            {
                using var native = new NativeObject(24);
                IAudioOutput output;
                if (asioMode)
                {
                    var asio = new AsioOutput();
                    var driver = (AsioDriver)Activator.CreateInstance(typeof(AsioDriver), Private, null, [native.Pointer], null)!;
                    Set(asio, "_driver", driver);
                    output = asio;
                }
                else
                {
                    var wasapi = new WasapiOutput(true, false);
                    Set(wasapi, "_client", new RawAudioClient(native.Pointer));
                    output = wasapi;
                }
                try
                {
                    Set(output, "_source", new SurroundSource(0x60F));
                    foreach (uint mask in new uint[] { 0, 0x3F, 0x60F })
                    {
                        bool attached = output is AsioOutput asio ? asio.AttachSource(new SurroundSource(mask)) : ((WasapiOutput)output).AttachSource(new SurroundSource(mask));
                        Require(attached == (mask == 0x60F), "layout change reused device buffers");
                    }
                }
                finally
                {
                    // The sentinel only proves AttachSource has an initialized backend;
                    // it has no native Stop/Release vtable and must not be disposed as one.
                    Set(output, asioMode ? "_driver" : "_client", null);
                    output.Dispose();
                }
            });
    }

    private sealed class SurroundSource(uint mask) : IRenderSource
    {
        public RenderKind Kind => RenderKind.Pcm;
        public int SampleRate => 48000;
        public int Channels => 6;
        public uint ChannelMask => mask;
        public void FillPcm(Span<double> buffer, int frames) => buffer.Clear();
        public void FillDop(Span<uint> buffer, int frames) => throw new NotSupportedException();
        public void FillDsdBytes(Span<byte> buffer, int frames) => throw new NotSupportedException();
    }

    private static void WriteSurroundWave(string path, uint mask, int channels = 6)
    {
        int dataBytes = 48000 * channels * 2;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(60 + dataBytes); writer.Write("WAVEfmt "u8);
        writer.Write(40); writer.Write((ushort)0xFFFE); writer.Write((ushort)channels);
        writer.Write(48000); writer.Write(48000 * channels * 2); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
        writer.Write((ushort)22); writer.Write((ushort)16); writer.Write(mask);
        writer.Write(SubFormats.Pcm.ToByteArray()); writer.Write("data"u8); writer.Write(dataBytes);
        for (int f = 0; f < 48000; f++) for (int ch = 0; ch < channels; ch++) writer.Write((short)((ch + 1) * 1000));
    }
}
