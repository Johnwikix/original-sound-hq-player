using AudioPlayer.Decode;
using AudioPlayer.Playback;

internal static unsafe partial class Program
{
    private static void RunPcmFileTests(string path)
    {
        foreach (int? rate in new int?[] { null, 44100 })
        foreach (int? channels in new int?[] { null, 2 })
            Run($"PCM file: rate={rate}, channels={channels}, decode/EOF/seek", () =>
            {
                using var decoder = new PcmDecoder();
                Require(decoder.Open(path, 88200, 0, forceRate: rate, forceChannels: channels), "Open failed");
                Require(rate == null || decoder.SampleRate == rate, "wrong output sample rate");
                Require(channels == null || decoder.Channels == channels, "wrong output channel count");
                var buffer = new double[4096 * decoder.Channels];
                long frames = 0;
                double peak = 0;
                int count;
                while ((count = decoder.Read(buffer)) > 0)
                {
                    Require(count <= 4096, "oversized read");
                    frames += count;
                    foreach (double sample in buffer.AsSpan(0, count * decoder.Channels))
                    {
                        Require(double.IsFinite(sample), "non-finite PCM");
                        peak = Math.Max(peak, Math.Abs(sample));
                    }
                    Require(frames < (decoder.TotalMs / 1000.0 + 5) * decoder.SampleRate, "decode exceeds duration");
                }
                Require(peak > 0.00001, "silent or empty decode");
                Require(Math.Abs(frames * 1000.0 / decoder.SampleRate - decoder.TotalMs) < 200,
                    $"truncated PCM: {frames} frames, expected {decoder.TotalMs} ms");
                Require(decoder.Read(buffer) == 0, "EOF not stable");
                Require(decoder.SeekToMs(decoder.TotalMs / 2) && decoder.Read(buffer) > 0, "seek after EOF failed");
                Require(decoder.SeekToMs(0) && decoder.Read(buffer) > 0, "rewind failed");
                Console.WriteLine($"Decoded {frames} frames, {decoder.SampleRate} Hz/{decoder.Channels}ch, peak={peak:F4}");
            });
        foreach (string mode in new[] { "WasapiShared", "ASIO", "WasapiExclusivePush", "WasapiExclusiveEvent" })
            Run($"PCM file: {mode} session advances", () =>
            {
                var engine = Engine(mode);
                engine.MusicUrl = path;
                using var session = (Session?)Invoke(engine, "OpenSession", path, false, null);
                Require(session != null && session.Kind == RenderKind.Pcm, "PCM session did not open");
                if (mode == "WasapiShared") Require(session!.Channels <= 2, "shared output did not downmix");
                CheckProgress(session!);
            });
    }
}
