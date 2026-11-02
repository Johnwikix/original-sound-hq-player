using System;
using System.Threading.Tasks;
namespace WinUIMusicPlayer.Services.AudioAnalysis;
/// <summary>不可变分析进度；发现数量在分页扫描中逐步增加。</summary>
public sealed record AnalysisProgress(bool Enabled = true, bool Running = false, int Completed = 0,
    int Discovered = 0, int Pending = 0, int Failed = 0, string CurrentTitle = "", string Phase = "Idle");
/// <summary>设置页消费唯一状态来源，不拥有后台任务。</summary>
public interface IAudioAnalysisSettingsSource
{
    AnalysisProgress Progress { get; }
    event Action? Changed;
    Task SetEnabledAsync(bool enabled);
}
