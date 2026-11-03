using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services.AudioAnalysis;
using WinUIMusicPlayer.Utils;
namespace WinUIMusicPlayer.ViewModel.Pages;
/// <summary>分析开关和进度的唯一UI适配；合并后台通知并在退出时解除订阅。</summary>
public sealed class AnalysisSettingsViewModel(IAudioAnalysisSettingsSource service, ILogger<AnalysisSettingsViewModel> logger) : ObservableObject
{
    private DispatcherQueue? _dispatcher;
    private AnalysisProgress _current = service.Progress;
    private int _posted;
    private bool _stopped;
    private bool? _requestedEnabled;
    private long _settingsRevision;
    private Task _writes = Task.CompletedTask;
    /// <summary>获取或设置持久分析开关。</summary>
    public bool Enabled
    {
        get => _requestedEnabled ?? _current.Enabled;
        set
        {
            if (_stopped || value == Enabled) return;
            _requestedEnabled = value;
            _writes = SetAfterAsync(_writes, value, ++_settingsRevision);
        }
    }
    /// <summary>获取本轮已完成的曲目数量。</summary>
    public double Completed => _current.Completed;
    /// <summary>获取已发现可分析项总数。</summary>
    public double Total => Math.Max(1, _current.Discovered);
    /// <summary>获取当前状态及数量。</summary>
    public string StatusText => string.Format(ToolUtils.GetString("AnalysisProgress"),
        _current.Completed, _current.Discovered, _current.Pending, _current.Failed);
    /// <summary>获取当前任务与阶段。</summary>
    public string CurrentText => string.Concat(ToolUtils.GetString("Analysis" + _current.Phase),
        _current.CurrentTitle.Length > 0 ? " · " + _current.CurrentTitle : "");
    /// <summary>启动UI通知，不创建分析工作。</summary>
    public void Start(DispatcherQueue dispatcher)
    {
        if (_dispatcher is not null || _stopped) return;
        _dispatcher = dispatcher;
        service.Changed += Changed;
        Changed();
    }
    private void Changed()
    {
        if (_stopped || Interlocked.Exchange(ref _posted, 1) != 0) return;
        if (_dispatcher?.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _posted, 0);
            if (_stopped) return;
            _current = service.Progress;
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(Completed));
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CurrentText));
        }) != true) Interlocked.Exchange(ref _posted, 0);
    }
    private async Task SetAfterAsync(Task previous, bool enabled, long revision)
    {
        await previous;
        try { await service.SetEnabledAsync(enabled); }
        catch (Exception ex) { logger.LogWarning(ex, "保存音频分析设置失败"); }
        finally
        {
            if (revision == _settingsRevision)
            {
                _current = service.Progress;
                _requestedEnabled = null;
            }
            Changed();
        }
    }
    /// <summary>取消订阅并等待已经接受的设置写入。</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        service.Changed -= Changed;
        await _writes;
    }
}
