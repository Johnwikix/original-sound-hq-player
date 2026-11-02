using System.Text;
using WinUIMusicPlayer.Services.AudioAnalysis;

internal static class Mp3CompatibilityChecks
{
    internal static async Task RunAsync(string? original = null)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "lyrics-tail.mp3");
        byte[] audio = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tone.mp3"));
        // 由合成正弦音生成的 MP3；模拟 Lyrics3 标签被 demuxer 当成最后一个音频包的真实边界。
        byte[] lyrics = Encoding.ASCII.GetBytes("LYRICSBEGINLYR00028[00:00.00]synthetic fixture\r\n000040LYRICS200");
        var tagged = new byte[audio.Length + lyrics.Length];
        audio.CopyTo(tagged, 0);
        lyrics.CopyTo(tagged, audio.Length);
        await File.WriteAllBytesAsync(fixture, tagged);
        try
        {
            var result = await SpectrogramWorkerClient.AnalyzeAsync(fixture, 2048, null, CancellationToken.None);
            if (result.SampleRate != 44100 || result.Combined.Max() < 100) throw new Exception("Trailing metadata lost valid audio.");
            Console.WriteLine("PASS: trailing Lyrics3 metadata does not discard decoded MP3 audio");
            if (original is not null)
            {
                foreach (int fft in new[] { 2048, 4096, 8192 })
                {
                    var song = await SpectrogramWorkerClient.AnalyzeAsync(original, fft, null, CancellationToken.None);
                    if (song.Duration < 290 || song.SampleRate != 44100 || song.Width != 1024) throw new Exception("Original MP3 result is incomplete.");
                    Console.WriteLine($"PASS: RIVER MP3 analyzes at FFT {fft} ({song.Duration:F3}s, {song.SampleRate} Hz)");
                }
            }
        }
        finally { File.Delete(fixture); }
    }
}
