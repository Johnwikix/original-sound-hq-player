using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using AudioPlayer.Decode;

namespace AudioPlayer.Playback;

/// <summary>串行后台扫描和版本化磁盘缓存；不写音乐文件、不占用播放解码器。</summary>
internal static class LoudnessScanner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<LoudnessMeasurement?> ScanAsync(string path, int rate, int channels,
        int dsdRate, int dsdGain, CancellationToken token, string? cacheDirectory = null)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(path, rate, channels, dsdRate, dsdGain, token, cacheDirectory), token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static LoudnessMeasurement? Scan(string path, int rate, int channels, int dsdRate, int dsdGain,
        CancellationToken token, string? cacheDirectory)
    {
        token.ThrowIfCancellationRequested();
        var file = new FileInfo(path);
        if (!file.Exists || channels is < 1 or > 2 || rate < 8000) return null;
        long length = file.Length, modified = file.LastWriteTimeUtc.Ticks;
        string key = $"v1|{file.FullName.ToUpperInvariant()}|{length}|{modified}|{rate}|{channels}|{dsdRate}|{dsdGain}";
        string directory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUIMusicPlayer", "LoudnessCache");
        string cache = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
        try
        {
            using var input = new BinaryReader(File.OpenRead(cache));
            if (input.ReadInt32() == 1)
            {
                var cached = new LoudnessMeasurement(input.ReadDouble(), input.ReadDouble());
                if (double.IsFinite(cached.IntegratedLufs) && cached.IntegratedLufs is >= -70 and <= 100
                    && double.IsFinite(cached.SamplePeak) && cached.SamplePeak > 0) return cached;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        using var decoder = new PcmDecoder();
        if (!decoder.Open(path, dsdRate, dsdGain, rate, channels)) return null;
        var meter = new LoudnessMeter(rate, channels);
        double[] scratch = ArrayPool<double>.Shared.Rent(16384 * channels);
        long frames = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = decoder.Read(scratch.AsSpan(0, 16384 * channels));
                if (count <= 0) break;
                frames += count;
                meter.Add(scratch.AsSpan(0, count * channels));
                // 限制损坏/异常超长输入；取消在每个解码块检查。
                if (frames > (long)rate * 24 * 60 * 60) return null;
            }
        }
        finally { ArrayPool<double>.Shared.Return(scratch); }
        token.ThrowIfCancellationRequested();
        // 解码器在错误时也可能返回 EOF，不把明显不完整的测量写入缓存。
        if (decoder.TotalMs > 0 && frames * 1000.0 / rate < decoder.TotalMs - 2000) return null;
        file.Refresh();
        if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != modified) return null;
        var result = meter.Finish();
        if (result is null) return null;
        try
        {
            Directory.CreateDirectory(directory);
            string temporary = cache + ".tmp";
            using (var output = new BinaryWriter(File.Create(temporary)))
            {
                output.Write(1); output.Write(result.IntegratedLufs); output.Write(result.SamplePeak);
            }
            File.Move(temporary, cache, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }
}
