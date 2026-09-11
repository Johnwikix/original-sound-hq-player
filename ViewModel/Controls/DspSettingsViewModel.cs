using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel.Controls;

/// <summary>音效设置状态源：偏好属性双向绑定控件并写回 AppSettings，
/// 播放端实际状态由可见时轮询刷新派生属性（可用性、生效、声道支持与响度分析）。</summary>
public partial class DspSettingsViewModel : ObservableObject
{
    private readonly IpcService _ipc;
    private readonly MusicDatabaseService _database;
    private readonly DispatcherQueueTimer _stateTimer, _commitTimer;
    private bool _syncing = true, _loaded, _refreshing, _available, _dirty;
    private DspState? _lastState;
    private int _settingsGeneration;

    public DspSettingsViewModel(IpcService ipc, MusicDatabaseService database)
    {
        _ipc = ipc;
        _database = database;
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
        _stateTimer = queue.CreateTimer();
        _stateTimer.Interval = TimeSpan.FromMilliseconds(500);
        _stateTimer.Tick += async (_, _) => await RefreshAsync();
        _commitTimer = queue.CreateTimer();
        _commitTimer.Interval = TimeSpan.FromMilliseconds(250);
        _commitTimer.IsRepeating = false;
        _commitTimer.Tick += async (_, _) => await CommitAsync();
    }

    /// <summary>DSP 总开关：写入本地偏好并立即提交，子开关随之按存储值重新呈现。</summary>
    public bool MasterEnabled
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (_syncing || !_loaded || !_available) return;
            _settingsGeneration++;
            AppSettings.Dsp = AppSettings.Dsp with { IsEnabled = value };
            LoadValues();
            EffectsActive = _available && value;
            _dirty = true;
            _commitTimer.Stop();
            _ = CommitAndRefreshAsync();
        }
    }

    public bool NormalizeLoudness
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) SettingChanged();
        }
    }

    /// <summary>NumberBox 输入非法时会推送 NaN，忽略该次写回，保留旧有效值。</summary>
    public double TargetLufs
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value) && double.IsFinite(value)) SettingChanged();
        }
    }

    public double HeadroomDb
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value) && double.IsFinite(value)) SettingChanged();
        }
    }

    /// <summary>左右平衡，百分比（-100 到 100），写回时换算为 -1 到 1。</summary>
    public double Balance
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) SettingChanged();
        }
    }

    public bool SwapChannels
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) SettingChanged();
        }
    }

    public bool Mono
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) SettingChanged();
        }
    }

    /// <summary>交叉馈送档位索引：0 关闭、1 轻度（0.2）、2 中度（0.4）。</summary>
    public int CrossfeedIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) SettingChanged();
        }
    }

    /// <summary>立体声宽度，百分比（0 到 150），写回时换算为 0 到 1.5。</summary>
    public double StereoWidth
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) SettingChanged();
        }
    }

    /// <summary>播放端在线且为 PCM 渲染，总开关仅此时可操作。</summary>
    public bool MasterAvailable { get => field; private set => SetProperty(ref field, value); }
    /// <summary>播放端已确认 DSP 生效，PCM 子设置仅此时可编辑。</summary>
    public bool EffectsActive { get => field; private set => SetProperty(ref field, value); }
    /// <summary>当前输出为立体声（或空闲未知）。</summary>
    public bool StereoSupported { get => field; private set => SetProperty(ref field, value); }
    public bool InfoOpen { get => field; private set => SetProperty(ref field, value); }
    public string InfoMessage { get => field; private set => SetProperty(ref field, value); } = "";
    public string AnalysisText { get => field; private set => SetProperty(ref field, value); } = "";

    /// <summary>视图加载：呈现本地偏好并立即查询一次实际状态。</summary>
    public async Task OnViewLoadedAsync()
    {
        _loaded = true;
        LoadValues();
        _stateTimer.Start();
        await RefreshAsync();
    }

    /// <summary>视图卸载：停止轮询并提交未落盘的修改。</summary>
    public async Task OnViewUnloadedAsync()
    {
        _loaded = false;
        _stateTimer.Stop();
        _commitTimer.Stop();
        await CommitAsync();
    }

    public async Task ResetAsync()
    {
        AppSettings.Dsp = new();
        LoadValues();
        _dirty = true;
        await CommitAsync();
    }

    /// <summary>从本地偏好重载编辑属性；子开关在效果未激活时按“关闭”呈现，与播放端实际状态一致。</summary>
    private void LoadValues()
    {
        _syncing = true;
        DspSettings settings = AppSettings.Dsp;
        bool effectsActive = _available && settings.IsEnabled;
        MasterEnabled = _available && settings.IsEnabled;
        NormalizeLoudness = effectsActive && settings.NormalizeLoudness;
        TargetLufs = settings.TargetLufs;
        HeadroomDb = settings.HeadroomDb;
        Balance = settings.Balance * 100;
        SwapChannels = effectsActive && settings.SwapChannels;
        Mono = effectsActive && settings.Mono;
        CrossfeedIndex = !effectsActive || settings.Crossfeed == 0 ? 0 : settings.Crossfeed <= 0.2 ? 1 : 2;
        StereoWidth = settings.StereoWidth * 100;
        _syncing = false;
    }

    /// <summary>轮询播放端实际状态；可用性或总开关变化时重载呈现，其余仅刷新派生属性。</summary>
    private async Task RefreshAsync()
    {
        if (!_loaded || _refreshing) return;
        _refreshing = true;
        int generation = _settingsGeneration;
        try
        {
            var state = await _ipc.GetDspStateAsync();
            if (!_loaded || generation != _settingsGeneration) return;
            bool available = state is { RenderKind: 0 };
            if (_available != available || _lastState == null || _lastState.Value.IsEnabled != state?.IsEnabled)
                LoadValues();
            _available = available;
            _lastState = state;
            MasterAvailable = available;
            bool effectsActive = available && state!.Value.IsEnabled && AppSettings.Dsp.IsEnabled;
            EffectsActive = effectsActive;
            StereoSupported = available && state!.Value.Channels is 0 or 2;
            bool unsupported = available && state!.Value.Channels != 0 && state.Value.Channels != 2;
            InfoOpen = !effectsActive || unsupported;
            InfoMessage = ToolUtils.GetString(state == null ? "DspStateUnavailable"
                : !available ? "DspBitstreamBypass" : !effectsActive ? "DspMasterBypass" : "DspStereoOnly");
            string key = state?.Loudness switch
            {
                LoudnessStatus.Analyzing => "DspAnalyzing",
                LoudnessStatus.Applied => "DspApplied",
                LoudnessStatus.PeakLimited => "DspPeakLimited",
                LoudnessStatus.Unavailable => "DspAnalysisUnavailable",
                LoudnessStatus.Failed => "DspAnalysisFailed",
                _ => "DspAnalysisOff"
            };
            AnalysisText = state is { Loudness: LoudnessStatus.Applied or LoudnessStatus.PeakLimited } value
                ? string.Format(ToolUtils.GetString(key), value.IntegratedLufs, value.GainDb)
                : ToolUtils.GetString(key);
        }
        finally { _refreshing = false; }
    }

    /// <summary>将编辑属性写回本地偏好（250ms 防抖提交，拖动滑条不产生中间 IO）。</summary>
    private void SettingChanged()
    {
        if (_syncing || !_loaded || !_available || !AppSettings.Dsp.IsEnabled) return;
        AppSettings.Dsp = new DspSettings
        {
            IsEnabled = AppSettings.Dsp.IsEnabled,
            NormalizeLoudness = NormalizeLoudness,
            TargetLufs = double.IsFinite(TargetLufs) ? TargetLufs : AppSettings.Dsp.TargetLufs,
            HeadroomDb = double.IsFinite(HeadroomDb) ? HeadroomDb : AppSettings.Dsp.HeadroomDb,
            Balance = Balance / 100, SwapChannels = SwapChannels, Mono = Mono,
            Crossfeed = CrossfeedIndex switch { 1 => 0.2, 2 => 0.4, _ => 0 },
            StereoWidth = StereoWidth / 100
        }.Sanitize();
        _dirty = true;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private async Task CommitAndRefreshAsync()
    {
        await CommitAsync();
        await RefreshAsync();
    }

    private async Task CommitAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        _ipc.UpdateDsp();
        await _database.SaveSettingAsync();
    }
}
