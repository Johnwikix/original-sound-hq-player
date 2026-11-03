using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioAnalysis;

namespace WinUIMusicPlayer.Services.AudioAnalysis;

/// <summary>拥有一次声谱图进程；取消或失败后先终止并等待进程，调用方随后才能释放输入租约。</summary>
public static class SpectrogramWorkerClient
{
    public static async Task<SpectrogramData> AnalyzeAsync(string path, int fftSize, IProgress<double>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AudioAnalysisWorker.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--spectrogram");
        using var process = Process.Start(start) ?? throw new IOException("Spectrum worker did not start.");
        try
        {
            var request = JsonSerializer.Serialize(new SpectrogramRequest(path, fftSize), SpectrogramJsonContext.Default.SpectrogramRequest);
            await process.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            var stream = process.StandardOutput.BaseStream;
            var header = new byte[24];
            while (true)
            {
                await stream.ReadExactlyAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
                if (header[0] == 0)
                {
                    await stream.ReadExactlyAsync(header.AsMemory(0, 8), deadline.Token).ConfigureAwait(false);
                    double value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(header));
                    if (!double.IsFinite(value) || value is < 0 or > 100) throw new InvalidDataException("Invalid spectrum progress.");
                    progress?.Report(value);
                    continue;
                }
                if (header[0] != 1) throw new InvalidDataException("Invalid spectrum packet.");
                await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                int rate = BinaryPrimitives.ReadInt32LittleEndian(header);
                int channels = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
                int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
                double duration = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)));
                if (rate is < 1000 or > 768000 || channels is < 1 or > 32 || size != fftSize || !SpectrogramData.ValidFftSize(size)
                    || width is < 1 or > SpectrogramData.MaxWidth || !double.IsFinite(duration) || duration <= 0 || duration > 86400)
                    throw new InvalidDataException("Invalid spectrum dimensions.");
                int length = checked(width * (size / 2 + 1));
                var combined = new byte[length];
                var left = channels > 1 ? new byte[length] : [];
                var right = channels > 1 ? new byte[length] : [];
                await stream.ReadExactlyAsync(combined, deadline.Token).ConfigureAwait(false);
                if (channels > 1)
                {
                    await stream.ReadExactlyAsync(left, deadline.Token).ConfigureAwait(false);
                    await stream.ReadExactlyAsync(right, deadline.Token).ConfigureAwait(false);
                }
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException("Spectrum worker failed.");
                token.ThrowIfCancellationRequested();
                return new(rate, channels, size, width, duration, combined, left, right);
            }
        }
        finally
        {
            try
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (System.ComponentModel.Win32Exception) { /* 保留进程所有权，继续等待真实退出。 */ }
            }
            finally { await process.WaitForExitAsync().ConfigureAwait(false); }
        }
    }
}
