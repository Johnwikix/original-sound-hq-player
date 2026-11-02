using System.Diagnostics;
using System.Text.Json;
using FFmpeg.AutoGen;
using WinUIMusicPlayer.AudioAnalysis;

// 标准MIR分析在独立进程运行。stdin关闭或空闲20秒退出，操作超时由父进程终止。
ffmpeg.RootPath = AppContext.BaseDirectory;
ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
using var process = Process.GetCurrentProcess();
try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
DspExtractor? extractor = null;
float[]? input = null, scratch = null;
while (true)
{
    string? line;
    try
    {
        // Console.In的同步包装在Windows上不保证取消；只允许一个待读任务，超时直接结束进程。
        line = await Task.Run(Console.ReadLine).WaitAsync(TimeSpan.FromSeconds(20));
    }
    catch (TimeoutException) { break; }
    if (line is null) break;
    if (line.Length > 32768) break;
    AnalysisReply reply;
    var cpuStart = process.TotalProcessorTime;
    try
    {
        var job = JsonSerializer.Deserialize(line, AnalysisJsonContext.Default.AnalysisRequest) ?? throw new InvalidDataException();
        if (!Path.IsPathFullyQualified(job.Path) || job.Path.StartsWith(@"\") || job.Path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        extractor ??= new();
        input ??= new float[320000];
        scratch ??= new float[65536];
        var vectors = new float[384];
        using (var decoder = new AudioDecoder(job.Path, scratch))
        {
            if (!double.IsFinite(decoder.Duration) || decoder.Duration <= 0) throw new InvalidDataException();
            for (int clip = 0; clip < 3; clip++)
            {
                double start = Math.Clamp(decoder.Duration * (.2 + clip * .3) - 5, 0, Math.Max(0, decoder.Duration - 10));
                if (decoder.ReadClip(start, input) <= 0) throw new InvalidDataException();
                extractor.Extract(input, vectors.AsSpan(clip * 128, 128));
            }
        }
        reply = new(vectors);

    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        reply = new(null, ex.GetType().Name);
        // 损坏文件同样背压，避免失败重试形成忙循环。
        await Task.Delay(250);
    }
    double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
    int restMs = (int)Math.Min(180000, Math.Ceiling(cpuMs * 9));
    if (restMs > 0) await Task.Delay(restMs);
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(reply, AnalysisJsonContext.Default.AnalysisReply));
    await Console.Out.FlushAsync();
}
