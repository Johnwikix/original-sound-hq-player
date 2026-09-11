using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.View.SubView.Settings;

/// <summary>集中编辑 PCM 音效偏好，持续显示播放端实际状态。</summary>
public sealed partial class DspSettingsControl : UserControl
{
    private readonly DispatcherQueueTimer _stateTimer, _commitTimer;
    private bool _syncing = true, _loaded, _refreshing, _available, _dirty;
    private DspState? _lastState;

    /// <summary>初始化音效设置和仅在可见时工作的状态轮询。</summary>
    public DspSettingsControl()
    {
        InitializeComponent();
        _stateTimer = DispatcherQueue.CreateTimer();
        _stateTimer.Interval = TimeSpan.FromMilliseconds(500);
        _stateTimer.Tick += async (_, _) => await RefreshAsync();
        _commitTimer = DispatcherQueue.CreateTimer();
        _commitTimer.Interval = TimeSpan.FromMilliseconds(250);
        _commitTimer.IsRepeating = false;
        _commitTimer.Tick += async (_, _) => await CommitAsync();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _loaded = true;
        LoadValues(false);
        _stateTimer.Start();
        await RefreshAsync();
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        _stateTimer.Stop();
        _commitTimer.Stop();
        await CommitAsync();
    }

    private void LoadValues(bool available)
    {
        _syncing = true;
        DspSettings settings = AppSettings.Dsp;
        NormalizeToggle.IsOn = available && settings.NormalizeLoudness;
        TargetBox.Value = settings.TargetLufs;
        HeadroomBox.Value = settings.HeadroomDb;
        BalanceSlider.Value = settings.Balance * 100;
        SwapToggle.IsOn = available && settings.SwapChannels;
        MonoToggle.IsOn = available && settings.Mono;
        CrossfeedCombo.SelectedIndex = !available || settings.Crossfeed == 0 ? 0 : settings.Crossfeed <= 0.2 ? 1 : 2;
        WidthSlider.Value = settings.StereoWidth * 100;
        TargetBox.IsEnabled = available && settings.NormalizeLoudness;
        _syncing = false;
    }

    private async Task RefreshAsync()
    {
        if (!_loaded || _refreshing) return;
        _refreshing = true;
        try
        {
            var state = await App.Services.GetRequiredService<IpcService>().GetDspStateAsync();
            if (!_loaded) return;
            bool available = state is { RenderKind: 0 };
            if (_available != available || _lastState == null) LoadValues(available);
            _available = available;
            _lastState = state;
            PcmSettings.IsEnabled = available;
            StereoSettings.IsEnabled = available && state!.Value.Channels is 0 or 2;
            bool unsupported = available && state!.Value.Channels != 0 && state.Value.Channels != 2;
            AvailabilityBar.IsOpen = !available || unsupported;
            AvailabilityBar.Message = ToolUtils.GetString(state == null ? "DspStateUnavailable"
                : !available ? "DspBitstreamBypass" : "DspStereoOnly");
            string key = state?.Loudness switch
            {
                LoudnessStatus.Analyzing => "DspAnalyzing",
                LoudnessStatus.Applied => "DspApplied",
                LoudnessStatus.PeakLimited => "DspPeakLimited",
                LoudnessStatus.Unavailable => "DspAnalysisUnavailable",
                LoudnessStatus.Failed => "DspAnalysisFailed",
                _ => "DspAnalysisOff"
            };
            AnalysisText.Text = state is { Loudness: LoudnessStatus.Applied or LoudnessStatus.PeakLimited } value
                ? string.Format(ToolUtils.GetString(key), value.IntegratedLufs, value.GainDb)
                : ToolUtils.GetString(key);
        }
        finally { _refreshing = false; }
    }

    private void Setting_Toggled(object sender, RoutedEventArgs e) => Changed();
    private void Number_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) => Changed();
    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Changed();
    private void Crossfeed_SelectionChanged(object sender, SelectionChangedEventArgs e) => Changed();

    private void Changed()
    {
        if (_syncing || !_loaded || !_available) return;
        AppSettings.Dsp = new DspSettings
        {
            NormalizeLoudness = NormalizeToggle.IsOn,
            TargetLufs = double.IsFinite(TargetBox.Value) ? TargetBox.Value : AppSettings.Dsp.TargetLufs,
            HeadroomDb = double.IsFinite(HeadroomBox.Value) ? HeadroomBox.Value : AppSettings.Dsp.HeadroomDb,
            Balance = BalanceSlider.Value / 100, SwapChannels = SwapToggle.IsOn, Mono = MonoToggle.IsOn,
            Crossfeed = CrossfeedCombo.SelectedIndex switch { 1 => 0.2, 2 => 0.4, _ => 0 },
            StereoWidth = WidthSlider.Value / 100
        }.Sanitize();
        TargetBox.IsEnabled = NormalizeToggle.IsOn;
        _dirty = true;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private async Task CommitAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        App.Services.GetRequiredService<IpcService>().UpdateDsp();
        await App.Services.GetRequiredService<MusicDatabaseService>().SaveSettingAsync();
    }

    private async void Reset_Click(object sender, RoutedEventArgs args)
    {
        AppSettings.Dsp = new();
        LoadValues(_available);
        _dirty = true;
        await CommitAsync();
    }

    private async void OpenEqualizer_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new EqualizerDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        dialog.EqualizerCommitted += static (_, _) => App.Services.GetRequiredService<IpcService>().UpdateEq();
        await dialog.ShowAsync();
    }
}
