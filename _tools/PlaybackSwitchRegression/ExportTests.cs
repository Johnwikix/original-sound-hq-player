using AudioPlayer.Decode;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Model;

internal static unsafe partial class Program
{
    private static void RunExportTests(string input)
    {
        foreach (string format in new[] { "wav", "flac", "mp3", "aac", "alac", "ogg", "opus" })
            Run($"Export {Path.GetFileName(input)} -> {format}", () =>
            {
                string output = Path.Combine(AppContext.BaseDirectory, $"export-{Guid.NewGuid():N}.{(format is "aac" or "alac" ? "m4a" : format)}");
                try
                {
                    using var source = new PcmDecoder();
                    Require(source.Open(input, 88200, 0), "source did not open");
                    int expectedChannels = format == "mp3" ? Math.Min(2, source.Channels) : source.Channels;
                    bool complete = false;
                    var converter = new FFmpegAudioConverter();
                    converter.progressEvent += (_, progress) => complete |= progress >= 100;
                    converter.Convert(input, output, format);
                    Require(complete, "conversion did not report completion");
                    using var decoded = new PcmDecoder();
                    Require(decoded.Open(output, 88200, 0), "export did not reopen");
                    Require(decoded.Channels == expectedChannels, $"expected {expectedChannels} channels, got {decoded.Channels}");
                    double[] buffer = new double[4096 * decoded.Channels];
                    long frames = 0;
                    double peak = 0;
                    int count;
                    while ((count = decoded.Read(buffer)) > 0)
                    {
                        frames += count;
                        foreach (double sample in buffer.AsSpan(0, count * decoded.Channels))
                        {
                            Require(double.IsFinite(sample), "non-finite exported PCM");
                            peak = Math.Max(peak, Math.Abs(sample));
                        }
                        Require(frames < (source.TotalMs / 1000.0 + 5) * decoded.SampleRate, "excessive output duration");
                    }
                    Require(peak > 0.00001, "empty/silent export");
                    Require(Math.Abs(frames * 1000.0 / decoded.SampleRate - source.TotalMs) < 200, "export duration mismatch");
                }
                finally { if (File.Exists(output)) File.Delete(output); }
            });
    }

    /// <summary>
    /// 转换器边界工况：拼接 MP3（文件中途切换采样率/声道数，必须重建重采样器而不是
    /// AVERROR_INPUT_CHANGED 整体失败）与损坏封面（解析不出宽高的截断 JPEG 必须跳过，
    /// 而不是把 0x0 尺寸封面流写进 m4a 导致 write_header EINVAL）。
    /// 用法：--test-export-edge &lt;44.1k wav&gt; &lt;48k wav&gt;
    /// </summary>
    private static void RunExportEdgeTests(string file44, string file48)
    {
        string work = Path.Combine(Path.GetTempPath(), $"mp-export-edge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            double DurationMs(string path)
            {
                using var d = new PcmDecoder();
                Require(d.Open(path, 88200, 0), $"did not open: {path}");
                return d.TotalMs;
            }

            Run("Glued MP3 mid-file param change", () =>
            {
                var c = new FFmpegAudioConverter();
                string p1 = Path.Combine(work, "glue-p1.mp3");
                string p2 = Path.Combine(work, "glue-p2.mp3");
                c.Convert(file44, p1, "mp3");
                c.Convert(file48, p2, "mp3");
                string glued = Path.Combine(work, "glued.mp3");
                File.WriteAllBytes(glued, File.ReadAllBytes(p1).Concat(File.ReadAllBytes(p2)).ToArray());

                double expect = DurationMs(p1) + DurationMs(p2);
                foreach (string format in new[] { "flac", "aac" })
                {
                    string output = Path.Combine(work, $"glued.{(format == "aac" ? "m4a" : format)}");
                    c.Convert(glued, output, format);
                    double got = DurationMs(output);
                    Require(Math.Abs(got - expect) < 300, $"{format}: duration {got:F0}ms != expected {expect:F0}ms");
                }
            });

            Run("Corrupt cover skipped (attached-pic)", () =>
            {
                var meta = new ConversionMetadata
                {
                    Title = "edge",
                    CoverBytes = [0xFF, 0xD8, 0xFF, 0xD9], // 仅 SOI+EOI，解析不出尺寸
                    CoverMime = "image/jpeg",
                };
                var c = new FFmpegAudioConverter();
                foreach (string format in new[] { "aac", "flac", "mp3" })
                {
                    string output = Path.Combine(work, $"badcover.{(format == "aac" ? "m4a" : format)}");
                    c.Convert(file44, output, format, metadata: meta);
                    Require(DurationMs(output) > 100, $"{format}: output too short");
                }
            });

            Run("Corrupt cover skipped (vorbis comment)", () =>
            {
                var meta = new ConversionMetadata
                {
                    Title = "edge",
                    CoverBytes = [0xFF, 0xD8, 0xFF, 0xD9],
                    CoverMime = "image/jpeg",
                };
                var c = new FFmpegAudioConverter();
                foreach (string format in new[] { "ogg", "opus" })
                {
                    string output = Path.Combine(work, $"badcover.{format}");
                    c.Convert(file44, output, format, metadata: meta);
                    Require(DurationMs(output) > 100, $"{format}: output too short");
                }
            });
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
