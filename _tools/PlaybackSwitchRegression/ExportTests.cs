using AudioPlayer.Decode;
using WinUIMusicPlayer.AudioConverters;

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
}
