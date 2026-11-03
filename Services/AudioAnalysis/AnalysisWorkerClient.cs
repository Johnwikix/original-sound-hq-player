using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioAnalysis;
namespace WinUIMusicPlayer.Services.AudioAnalysis;
/// <summary>串行拥有一个按需启动的进程；取消必须先确认进程退出再释放输入租约。</summary>
public sealed class AnalysisWorkerClient : IAsyncDisposable
{
    private Process? _process;
    /// <summary>发送单个固定任务；没有网络或播放进程依赖。</summary>
    public async Task<float[]> AnalyzeAsync(string path, CancellationToken token)
    {
        if (_process is null || _process.HasExited)
        {
            await DisposeAsync();
            string executable = Path.Combine(AppContext.BaseDirectory, "AudioAnalysisWorker.exe");
            _process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                WorkingDirectory = AppContext.BaseDirectory
            }) ?? throw new IOException("Analysis worker did not start.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new AnalysisRequest(path), AnalysisJsonContext.Default.AnalysisRequest).AsMemory(), deadline.Token);
            await _process.StandardInput.FlushAsync(deadline.Token);
            string line = await _process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("Analysis worker exited.");
            if (line.Length > 32768) throw new InvalidDataException("Invalid analysis reply.");
            var reply = JsonSerializer.Deserialize(line, AnalysisJsonContext.Default.AnalysisReply);
            if (reply?.Features is not { Length: 384 } features) throw new InvalidDataException("Audio analysis failed.");
            foreach (float value in features) if (!float.IsFinite(value)) throw new InvalidDataException("Invalid audio feature.");
            return features;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }
    /// <summary>先停止并等待真实进程，再释放管道；重复调用安全。</summary>
    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception)
            {
                // 即使终止请求失败，也保留所有权并等待受限任务/空闲超时真正退出。
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync();
        }
        finally { process.Dispose(); }
    }
}
