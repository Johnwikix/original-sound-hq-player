using System.Text.Json;

namespace WinUIMusicPlayer.AudioAnalysis;

internal static class SpectrogramWorker
{
    /// <summary>一次独立任务，二进制 stdout 只返回进度与有界强度矩阵，不与 MIR 的 JSON 会话混用。</summary>
    internal static int Run()
    {
        try
        {
            string? line = Console.ReadLine();
            if (line is null || line.Length > 32768) return 1;
            var request = JsonSerializer.Deserialize(line, SpectrogramJsonContext.Default.SpectrogramRequest);
            if (request is null || !Path.IsPathFullyQualified(request.Path) || !SpectrogramData.ValidFftSize(request.FftSize)
                || request.Path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return 1;
            using var writer = new BinaryWriter(Console.OpenStandardOutput());
            using var decoder = new SpectrogramDecoder(request.Path);
            var result = decoder.Analyze(request.FftSize, value =>
            {
                writer.Write((byte)0);
                writer.Write(value);
                writer.Flush();
            });
            writer.Write((byte)1);
            writer.Write(result.SampleRate);
            writer.Write(result.Channels);
            writer.Write(result.FftSize);
            writer.Write(result.Width);
            writer.Write(result.Duration);
            writer.Write(result.Combined);
            writer.Write(result.Left);
            writer.Write(result.Right);
            writer.Flush();
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return 1; }
    }
}
