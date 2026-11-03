using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioAnalysis;

namespace WinUIMusicPlayer.Services;

/// <summary>UI 线程捕获的不可变曲目身份，不向工作线程传递可变 Music。</summary>
public sealed record SpectrogramInput(int MusicId, int SourceId, string Path);

public interface ISpectrogramService
{
    /// <summary>null 表示远程文件没有完整缓存；取消完成时原生读取和输入租约均已结束。</summary>
    Task<SpectrogramData?> AnalyzeAsync(SpectrogramInput input, int fftSize, IProgress<double> progress, CancellationToken token);
}
