using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public sealed partial class ConvolutionCurveViewModel : ObservableObject
{
    private readonly IpcService _ipc;
    private readonly CurvePresetService _store;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private readonly DspSettings _initial;
    private readonly List<CurvePoint> _points;
    private bool _syncing, _open;
    private string? _editorOutputId;
    private bool _deviceMode;
    private string _preferredPresetName = "";
    public event Action? PreviewChanged;
    public event Action? PresetSaved;
    public event Action? PresetDeleted;
    public ObservableCollection<string> Nodes { get; } = [];
    public ObservableCollection<CurvePreset> Presets { get; } = [];
    public CurvePoint[] Points => _points.ToArray();
    public string TitleText => ToolUtils.GetString("CurveEditorTitle");
    public string ApplyText => ToolUtils.GetString("CurveApply");
    public string CancelText => ToolUtils.GetString("CurveCancel");
    public string PresetPlaceholder => ToolUtils.GetString(Presets.Count > 0 ? "CurveCustom" : "CurvePresetEmpty");
    public bool CanAdd => _points.Count < CorrectionCurve.MaxPoints;
    public bool CanRemove => _points.Count > 2;
    public bool ManualGainEnabled => !AutoPreamp;
    public bool CanSavePreset => !IsPresetBusy;
    public bool CanDeletePreset => !IsPresetBusy && SelectedPreset != null;
    public bool CanUpdatePreset => CanDeletePreset && SelectedPreset!.Points != CorrectionCurve.Encode(_points);
    public bool IsPresetBusy
    {
        get => field;
        private set { if (SetProperty(ref field, value)) NotifyPresetActions(); }
    }
    public string DeletePresetConfirmation => string.Format(ToolUtils.GetString("CurveDeletePresetConfirmation"), SelectedPreset?.Name ?? "");
    public int SelectedNode { get => field; set { if (!_syncing && (value < 0 || value >= _points.Count)) return; if (SetProperty(ref field, value) && !_syncing) SyncNodes(); } }
    public double Frequency { get => field; set { if (SetProperty(ref field, value) && !_syncing && double.IsFinite(value)) MovePoint(SelectedNode, value, Gain); } }
    public double Gain { get => field; set { if (SetProperty(ref field, value) && !_syncing && double.IsFinite(value)) MovePoint(SelectedNode, Frequency, value); } }
    public double PreampDb { get => field; set { if (SetProperty(ref field, value) && double.IsFinite(value)) Changed(); } }
    public bool AutoPreamp { get => field; set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(ManualGainEnabled)); Changed(); } } }
    public bool Audition { get => field; set { if (SetProperty(ref field, value)) Changed(); } } = true;
    public bool CanAudition { get => field; private set => SetProperty(ref field, value); }
    public string PresetName { get => field; set => SetProperty(ref field, value); } = "";
    public string ErrorMessage { get => field; private set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(HasError)); } } = "";
    public bool HasError => ErrorMessage.Length > 0;
    public CurvePreset? SelectedPreset
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value)) return;
            NotifyPresetActions();
            OnPropertyChanged(nameof(DeletePresetConfirmation));
            if (_syncing || value == null) return;
            _points.Clear(); _points.AddRange(CorrectionCurve.Parse(value.Points));
            SyncNodes(); Changed();
        }
    }
    public DspSettings Draft => _initial with { ConvolutionSource = ConvolutionSource.Curve,
        CurvePoints = CorrectionCurve.Encode(_points),
        CurvePresetName = SelectedPreset?.Points == CorrectionCurve.Encode(_points) ? SelectedPreset.Name : "", ConvolutionEnabled = true,
        AutoPreamp = AutoPreamp, HeadroomDb = double.IsFinite(PreampDb) ? PreampDb : _initial.HeadroomDb };

    public ConvolutionCurveViewModel(IpcService ipc, CurvePresetService store)
    {
        _ipc = ipc; _store = store;
        _initial = AppSettings.Dsp.ToUnifiedGain();
        _preferredPresetName = _initial.CurvePresetName;
        _points = CorrectionCurve.Parse(_initial.CurvePoints).ToList();
        _syncing = true; AutoPreamp = _initial.AutoPreamp == true; PreampDb = _initial.HeadroomDb; _syncing = false;
        _queue = DispatcherQueue.GetForCurrentThread();
        _timer = _queue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(150); _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { if (_open) _ipc.PreviewDsp(Audition ? Draft : _initial with { ConvolutionEnabled = false }); };
        SyncNodes();
    }
    public async Task OpenAsync()
    {
        _open = true; _ipc.DspStateChanged += PlaybackChanged; ApplyPlaybackState(force: true);
        if (!AppSettings.DeviceCorrections.Enabled) Changed();
        IsPresetBusy = true;
        try
        {
            var presets = await _store.LoadAsync();
            if (!_open) return;
            Presets.Clear(); foreach (var preset in presets) Presets.Add(preset);
            MatchPreset(); OnPropertyChanged(nameof(PresetPlaceholder));
        }
        catch { if (_open) ErrorMessage = ToolUtils.GetString("CurvePresetError"); }
        finally { IsPresetBusy = false; }
    }
    /// <summary>将设备绑定载入当前编辑草稿，保留全局设置直到用户应用。</summary>
    public void LoadCorrectionDraft(DspSettings settings) => LoadCorrectionDraft(settings, preview: true);

    private void LoadCorrectionDraft(DspSettings settings, bool preview)
    {
        if (!CorrectionCurve.UsesCurve(settings)) return;
        _points.Clear();
        _points.AddRange(CorrectionCurve.Parse(settings.CurvePoints));
        _preferredPresetName = settings.CurvePresetName;
        _syncing = true;
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == settings.CurvePresetName && p.Points == settings.CurvePoints)
            ?? Presets.FirstOrDefault(p => p.Points == settings.CurvePoints);
        _syncing = false;
        SyncNodes();
        if (preview) Changed();
        else NotifyPresetActions();
    }

    public void Close(bool apply)
    {
        _open = false; _timer.Stop(); _ipc.DspStateChanged -= PlaybackChanged;
        _ipc.PreviewDsp(apply ? Draft : AppSettings.Dsp);
    }
    private void PlaybackChanged() => _queue.TryEnqueue(() => ApplyPlaybackState());

    /// <summary>设备模式变化时立即同步；普通响度状态通知不会覆盖用户正在编辑的草稿。</summary>
    public void RefreshOutputCorrection() => ApplyPlaybackState(force: true);

    private void ApplyPlaybackState(bool force = false)
    {
        if (!_open) return;
        var state = _ipc.CurrentDspState?.State;
        CanAudition = state is { RenderKind: 0, IsEnabled: true, Channels: <= 2 };
        string id = state?.OutputDeviceId ?? "";
        bool mode = AppSettings.DeviceCorrections.Enabled;
        bool changed = force || id != _editorOutputId || mode != _deviceMode;
        _editorOutputId = id;
        _deviceMode = mode;
        if (!changed) return;
        // 自动跟随时停止旧草稿试听，保持内核按实际端点应用绑定；不写回全局草稿。
        _timer.Stop();
        _ipc.UpdateDsp();
        if (!mode) return;
        var binding = AppSettings.DeviceCorrections.Find(id);
        if (binding != null && CorrectionCurve.UsesCurve(binding.Settings))
            LoadCorrectionDraft(binding.Settings, preview: false);
        else
            LoadCorrectionDraft(new DspSettings { ConvolutionSource = ConvolutionSource.Curve,
                CurvePoints = CorrectionCurve.Flat }, preview: false);
    }
    private void MatchPreset()
    {
        _syncing = true;
        string encoded = CorrectionCurve.Encode(_points);
        if (SelectedPreset?.Points != encoded) SelectedPreset = Presets.FirstOrDefault(p => p.Points == encoded && p.Name == _preferredPresetName)
            ?? Presets.FirstOrDefault(p => p.Points == encoded);
        _syncing = false;
    }
    private void SyncNodes()
    {
        _syncing = true;
        int selected = Math.Clamp(SelectedNode, 0, _points.Count - 1);
        Nodes.Clear(); for (int i = 0; i < _points.Count; i++) Nodes.Add($"{i + 1} · {_points[i].Frequency:0.#} Hz");
        SelectedNode = selected; Frequency = _points[selected].Frequency; Gain = _points[selected].GainDb;
        _syncing = false;
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(CanRemove));
        AddPointCommand.NotifyCanExecuteChanged(); RemovePointCommand.NotifyCanExecuteChanged();
        PreviewChanged?.Invoke();
    }
    public void MovePoint(int index, double frequency, double gain)
    {
        if (index < 0 || index >= _points.Count || !double.IsFinite(frequency) || !double.IsFinite(gain)) return;
        double min = index == 0 ? 20 : _points[index - 1].Frequency + 0.05;
        double max = index == _points.Count - 1 ? 20000 : _points[index + 1].Frequency - 0.05;
        if (min > max) return;
        _points[index] = new(Math.Clamp(Math.Round(frequency, 2), min, max), Math.Round(Math.Clamp(gain, -12, 12), 2));
        _syncing = true; SelectedNode = index; _syncing = false;
        SyncNodes(); Changed();
    }
    private void Changed()
    {
        if (_syncing || !_open) return;
        ErrorMessage = ""; NotifyPresetActions(); PreviewChanged?.Invoke(); _timer.Stop(); _timer.Start();
    }
    private void NotifyPresetActions()
    {
        OnPropertyChanged(nameof(CanSavePreset));
        OnPropertyChanged(nameof(CanUpdatePreset));
        OnPropertyChanged(nameof(CanDeletePreset));
        SavePresetCommand.NotifyCanExecuteChanged();
        UpdatePresetCommand.NotifyCanExecuteChanged();
        DeletePresetCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddPoint()
    {
        int left = 0;
        for (int i = 1; i < _points.Count - 1; i++)
            if (_points[i + 1].Frequency / _points[i].Frequency > _points[left + 1].Frequency / _points[left].Frequency) left = i;
        double hz = Math.Round(Math.Sqrt(_points[left].Frequency * _points[left + 1].Frequency), 2);
        if (hz - _points[left].Frequency < 0.02 || _points[left + 1].Frequency - hz < 0.02) return;
        _points.Insert(left + 1, new(hz, CorrectionCurve.Evaluate(Points, hz)));
        SelectedNode = left + 1; SyncNodes(); Changed();
    }
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemovePoint() { _points.RemoveAt(SelectedNode); SyncNodes(); Changed(); }
    [RelayCommand]
    private void ResetFlat() { _points.Clear(); _points.AddRange(CorrectionCurve.Parse(CorrectionCurve.Flat)); SyncNodes(); Changed(); }
    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private async Task SavePresetAsync()
    {
        if (!CanSavePreset) return;
        string name = PresetName.Trim();
        if (name.Length == 0 || name.Length > 80 || Presets.Count >= 100 || Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        { ErrorMessage = ToolUtils.GetString("CurvePresetNameError"); return; }
        var preset = new CurvePreset(name, Draft.CurvePoints);
        IsPresetBusy = true;
        try
        {
            await _store.SaveAsync([.. Presets, preset]);
            Presets.Add(preset);
            if (Draft.CurvePoints == preset.Points) { _syncing = true; SelectedPreset = preset; _syncing = false; }
            ErrorMessage = "";
            OnPropertyChanged(nameof(PresetPlaceholder)); PresetSaved?.Invoke();
        }
        catch { ErrorMessage = ToolUtils.GetString("CurvePresetError"); }
        finally { IsPresetBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanUpdatePreset))]
    private async Task UpdatePresetAsync()
    {
        if (!CanUpdatePreset) return;
        var selected = SelectedPreset!;
        int index = Presets.IndexOf(selected);
        if (index < 0) return;
        var updated = selected with { Points = CorrectionCurve.Encode(_points) };
        var snapshot = Presets.ToList(); snapshot[index] = updated;
        IsPresetBusy = true;
        try
        {
            await _store.SaveAsync(snapshot);
            _syncing = true;
            Presets[index] = updated;
            SelectedPreset = updated;
            _syncing = false;
            Changed();
            ErrorMessage = "";
        }
        catch { ErrorMessage = ToolUtils.GetString("CurvePresetError"); }
        finally { _syncing = false; IsPresetBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private async Task DeletePresetAsync()
    {
        if (!CanDeletePreset) return;
        var selected = SelectedPreset!;
        var snapshot = Presets.Where(p => p != selected).ToList();
        IsPresetBusy = true;
        try
        {
            await _store.SaveAsync(snapshot);
            _syncing = true;
            SelectedPreset = null;
            Presets.Remove(selected);
            _syncing = false;
            // Deleting a stored preset leaves the current draft available as a custom curve.
            Changed();
            ErrorMessage = "";
            OnPropertyChanged(nameof(PresetPlaceholder));
            PresetDeleted?.Invoke();
        }
        catch { ErrorMessage = ToolUtils.GetString("CurvePresetError"); }
        finally { _syncing = false; IsPresetBusy = false; }
    }
}
