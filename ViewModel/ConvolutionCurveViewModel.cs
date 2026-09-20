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
    private readonly MusicDatabaseService _database;
    private readonly LicenseService _license;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<CurvePoint> _points;
    private bool _syncing, _open, _stopped;
    private Task _openTask = Task.CompletedTask;
    private string? _editorOutputId;
    private long _editorOutputGeneration;
    private bool _deviceMode;
    private bool _offlineEditing;
    private bool _dirty, _writingSettings;
    private Task _commitTask = Task.CompletedTask;
    private string _preferredPresetName = "";
    public event Action? PreviewChanged;
    public event Action? PresetSaved;
    public event Action? PresetDeleted;
    public ObservableCollection<string> Nodes { get; } = [];
    public ObservableCollection<CurvePreset> Presets { get; } = [];
    public CurvePoint[] Points => _points.ToArray();
    public string TitleText => ToolUtils.GetString("CurveEditorTitle");
    public string CloseText => ToolUtils.GetString("CloseButton");
    public bool CanEdit => !_stopped && !_license.IsFeatureRestricted(LicenseFeature.Convolution);
    public bool IsOfflineEditing => _offlineEditing;
    public string OfflineEditingText => ToolUtils.GetString("CurveOfflineEditing");
    public string PresetPlaceholder => ToolUtils.GetString(Presets.Count > 0 ? "CurveCustom" : "CurvePresetEmpty");
    public bool CanAdd => CanEdit && _points.Count < CorrectionCurve.MaxPoints;
    public bool CanRemove => CanEdit && _points.Count > 2;
    public bool PreampEditable => CanEdit && !_license.IsFeatureRestricted(LicenseFeature.Preamp);
    public bool ManualGainEnabled => PreampEditable && !AutoPreamp;
    public bool CanSavePreset => CanEdit && !IsPresetBusy;
    public bool CanDeletePreset => !_license.IsFeatureRestricted(LicenseFeature.Convolution) && !IsPresetBusy && SelectedPreset != null;
    public bool CanUpdatePreset => CanDeletePreset && SelectedPreset!.Points != CorrectionCurve.Encode(_points);
    public bool CanApplyPreset => CanEdit && CanDeletePreset && (CanUpdatePreset || AppSettings.IsCustomCorrectionDraft);
    public bool IsPresetBusy
    {
        get => field;
        private set { if (SetProperty(ref field, value)) NotifyPresetActions(); }
    }
    public string DeletePresetConfirmation => string.Format(ToolUtils.GetString("CurveDeletePresetConfirmation"), SelectedPreset?.Name ?? "");
    public int SelectedNode { get => field; set { if (!_syncing && (value < 0 || value >= _points.Count)) return; if (SetProperty(ref field, value) && !_syncing) SyncNodes(); } }
    public double Frequency { get => field; set { if (SetProperty(ref field, value) && !_syncing && double.IsFinite(value)) MovePoint(SelectedNode, value, Gain); } }
    public double Gain { get => field; set { if (SetProperty(ref field, value) && !_syncing && double.IsFinite(value)) MovePoint(SelectedNode, Frequency, value); } }
    public double PreampDb { get => field; set { if (!_syncing && !PreampEditable) { OnPropertyChanged(); return; } if (SetProperty(ref field, value) && double.IsFinite(value)) Changed(custom: AppSettings.IsCustomCorrectionDraft); } }
    public bool AutoPreamp { get => field; set { if (!_syncing && !PreampEditable) { OnPropertyChanged(); return; } if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(ManualGainEnabled)); Changed(custom: AppSettings.IsCustomCorrectionDraft); } } }
    public string PresetName { get => field; set => SetProperty(ref field, value); } = "";
    public string ErrorMessage { get => field; private set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(HasError)); } } = "";
    public bool HasError => ErrorMessage.Length > 0;
    public CurvePreset? SelectedPreset
    {
        get => field;
        set
        {
            if (!_syncing && !CanEdit) return;
            if (!SetProperty(ref field, value)) return;
            NotifyPresetActions();
            OnPropertyChanged(nameof(DeletePresetConfirmation));
            if (_syncing || value == null) return;
            _points.Clear(); _points.AddRange(CorrectionCurve.Parse(value.Points));
            SyncNodes(); Changed(custom: false);
        }
    }
    public DspSettings Draft
    {
        get
        {
            // 每次快照只编码一次；其余音效沿用当前全局设置。
            string points = CorrectionCurve.Encode(_points);
            return AppSettings.Dsp.ToUnifiedGain() with { ConvolutionSource = ConvolutionSource.Curve,
                CurvePoints = points,
                CurvePresetName = SelectedPreset?.Points == points ? SelectedPreset.Name : "", ConvolutionEnabled = true,
                AutoPreamp = AutoPreamp, HeadroomDb = double.IsFinite(PreampDb) ? PreampDb : AppSettings.Dsp.HeadroomDb };
        }
    }

    public ConvolutionCurveViewModel(IpcService ipc, CurvePresetService store, MusicDatabaseService database, LicenseService license)
    {
        _ipc = ipc; _store = store; _database = database; _license = license;
        var initial = AppSettings.Dsp.ToUnifiedGain();
        _preferredPresetName = initial.CurvePresetName;
        _points = CorrectionCurve.Parse(initial.CurvePoints).ToList();
        _syncing = true; AutoPreamp = initial.AutoPreamp == true; PreampDb = initial.HeadroomDb; _syncing = false;
        _queue = DispatcherQueue.GetForCurrentThread();
        _timer = _queue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(500); _timer.IsRepeating = false;
        _timer.Tick += async (_, _) => await FlushAsync();
        SyncNodes();
    }
    public Task OpenAsync()
    {
        if (_stopped || _open) return _openTask;
        return _openTask = OpenCoreAsync();
    }
    private async Task OpenCoreAsync()
    {
        _offlineEditing = false;
        _deviceMode = false;
        _open = true; _ipc.DspStateChanged += PlaybackChanged;
        _license.StateChanged += LicenseChanged;
        AppSettings.AudioResponseChanged += SettingsChanged;
        ApplyPlaybackState(force: true);
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
    /// <summary>将选定曲线立即写入当前编辑目标。</summary>
    public void LoadCorrectionDraft(DspSettings settings) => LoadCorrectionDraft(settings, commit: true);

    private void LoadCorrectionDraft(DspSettings settings, bool commit)
    {
        if (commit && !CanEdit) return;
        if (!CorrectionCurve.UsesCurve(settings)) return;
        _points.Clear();
        _points.AddRange(CorrectionCurve.Parse(settings.CurvePoints));
        _preferredPresetName = settings.CurvePresetName;
        _syncing = true;
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == settings.CurvePresetName && p.Points == settings.CurvePoints)
            ?? Presets.FirstOrDefault(p => p.Points == settings.CurvePoints);
        _syncing = false;
        SyncNodes();
        if (commit) Changed(custom: false);
        else NotifyPresetActions();
    }

    public async Task StopAsync()
    {
        _stopped = true;
        _timer.Stop();
        _open = false;
        _ipc.DspStateChanged -= PlaybackChanged;
        _license.StateChanged -= LicenseChanged;
        AppSettings.AudioResponseChanged -= SettingsChanged;
        await Task.WhenAll(_openTask,
            SavePresetCommand.ExecutionTask ?? Task.CompletedTask,
            UpdatePresetCommand.ExecutionTask ?? Task.CompletedTask,
            DeletePresetCommand.ExecutionTask ?? Task.CompletedTask,
            ApplyPresetCommand.ExecutionTask ?? Task.CompletedTask);
        await FlushAsync();
        if (_dirty) throw new System.IO.IOException("Curve editor final commit failed.");
    }

    public async Task<bool> CloseAsync()
    {
        _timer.Stop();
        await Task.WhenAll(_openTask,
            SavePresetCommand.ExecutionTask ?? Task.CompletedTask,
            UpdatePresetCommand.ExecutionTask ?? Task.CompletedTask,
            DeletePresetCommand.ExecutionTask ?? Task.CompletedTask,
            ApplyPresetCommand.ExecutionTask ?? Task.CompletedTask);
        await FlushAsync();
        if (_dirty) return false;
        _open = false;
        _ipc.DspStateChanged -= PlaybackChanged;
        _license.StateChanged -= LicenseChanged;
        AppSettings.AudioResponseChanged -= SettingsChanged;
        return true;
    }
    private void PlaybackChanged() => _queue.TryEnqueue(() => ApplyPlaybackState());
    private void LicenseChanged() => _queue.TryEnqueue(() =>
    {
        if (!_open) return;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(ManualGainEnabled));
        OnPropertyChanged(nameof(PreampEditable));
        AddPointCommand.NotifyCanExecuteChanged();
        RemovePointCommand.NotifyCanExecuteChanged();
        ResetFlatCommand.NotifyCanExecuteChanged();
        NotifyPresetActions();
    });
    private void SettingsChanged(object? sender, EventArgs args)
    {
        if (!_writingSettings) _queue.TryEnqueue(() => ApplyPlaybackState(force: true));
    }

    /// <summary>设备模式变化时立即同步；普通响度状态通知不会重载编辑器。</summary>
    public void RefreshOutputCorrection() => ApplyPlaybackState(force: true);

    private void ApplyPlaybackState(bool force = false)
    {
        if (!_open) return;
        var state = _ipc.CurrentDspState?.State;
        string id = state?.OutputDeviceId ?? "";
        bool mode = AppSettings.DeviceCorrections.Enabled;
        long generation = state?.OutputGeneration ?? 0;
        bool outputChanged = !string.Equals(id, _editorOutputId, StringComparison.OrdinalIgnoreCase)
            || generation != _editorOutputGeneration;
        bool changed = force || outputChanged || mode != _deviceMode;
        _editorOutputId = id;
        _editorOutputGeneration = generation;
        _deviceMode = mode;
        _offlineEditing = mode && string.IsNullOrEmpty(id);
        OnPropertyChanged(nameof(IsOfflineEditing));
        if (!changed) return;

        // 会话层统一解析：自定义草稿跨输出，显式应用的预设只覆盖当前输出代次。
        var effective = AppSettings.ResolveResponseSettings(id, generation).ToUnifiedGain();
        _syncing = true;
        AutoPreamp = effective.AutoPreamp == true;
        PreampDb = effective.HeadroomDb;
        _syncing = false;
        var curve = CorrectionCurve.UsesCurve(effective) && (!mode || AppSettings.DeviceCorrections.Find(id) != null
            || AppSettings.TryGetLiveCorrection(id, generation, out _))
            ? effective : effective with { ConvolutionSource = ConvolutionSource.Curve,
                CurvePoints = CorrectionCurve.Flat, CurvePresetName = "" };
        // 普通通知及首播不清除未保存曲线的预设选择，保留显式“重新应用”的目标。
        if (curve.CurvePoints == CorrectionCurve.Encode(_points) && AppSettings.IsCustomCorrectionDraft)
        {
            NotifyPresetActions();
            PreviewChanged?.Invoke();
            return;
        }
        LoadCorrectionDraft(curve, commit: false);
    }
    private void MatchPreset()
    {
        _syncing = true;
        string encoded = CorrectionCurve.Encode(_points);
        if (AppSettings.IsCustomCorrectionDraft && AppSettings.CustomCorrectionPresetName.Length > 0)
            SelectedPreset = Presets.FirstOrDefault(p => p.Name == AppSettings.CustomCorrectionPresetName);
        else if (SelectedPreset?.Points != encoded) SelectedPreset = Presets.FirstOrDefault(p => p.Points == encoded && p.Name == _preferredPresetName)
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
        if (!CanEdit || index < 0 || index >= _points.Count || !double.IsFinite(frequency) || !double.IsFinite(gain)) return;
        double min = index == 0 ? 20 : _points[index - 1].Frequency + 0.05;
        double max = index == _points.Count - 1 ? 20000 : _points[index + 1].Frequency - 0.05;
        if (min > max) return;
        _points[index] = new(Math.Clamp(Math.Round(frequency, 2), min, max), Math.Round(Math.Clamp(gain, -12, 12), 2));
        _syncing = true; SelectedNode = index; _syncing = false;
        SyncNodes(); Changed();
    }
    private void Changed(bool custom = true)
    {
        if (_syncing || !_open || !CanEdit) return;
        var previous = AppSettings.Dsp;
        var draft = LicensePolicy.PreserveRestrictedPreferences(Draft.Sanitize(), AppSettings.Dsp, _license.RestrictedFeatures);
        _writingSettings = true;
        try
        {
            if (_deviceMode)
            {
                var state = _ipc.CurrentDspState?.State;
                if (custom)
                    AppSettings.SetCustomCorrectionDraft(draft, SelectedPreset?.Name ?? "");
                else
                    AppSettings.SetLiveCorrection(state?.OutputDeviceId ?? "", state?.OutputGeneration ?? 0, draft);
                AppSettings.Dsp = AppSettings.Dsp.ToUnifiedGain() with { ConvolutionEnabled = true,
                    AutoPreamp = draft.AutoPreamp, HeadroomDb = draft.HeadroomDb };
            }
            else AppSettings.Dsp = draft;
        }
        finally { _writingSettings = false; }
        // 离线拖动仅更新已有草稿，不触发磁盘或 IPC；全局预放大变化仍须保存。
        if (!_deviceMode || !string.IsNullOrEmpty(_ipc.CurrentDspState?.State.OutputDeviceId) || AppSettings.Dsp != previous)
        {
            _dirty = true;
            _timer.Stop();
            _timer.Start();
        }
        ErrorMessage = "";
        NotifyPresetActions();
        PreviewChanged?.Invoke();
    }

    /// <summary>Coalesce edits, publish the latest shared state, then save it without restoring stale snapshots.</summary>
    public Task FlushAsync()
    {
        if (!_commitTask.IsCompleted) return _commitTask;
        return _commitTask = CommitAsync();
    }

    private async Task CommitAsync()
    {
        while (_dirty)
        {
            _dirty = false;
            var publish = PublishAsync();
            try
            {
                await _database.SaveSettingAsync(throwOnError: true);
                ErrorMessage = await publish ? "" : ToolUtils.GetString("DspDeviceApplyError");
            }
            catch
            {
                await publish;
                _dirty = true;
                ErrorMessage = ToolUtils.GetString("CurveLiveSaveError");
                return;
            }
        }
    }

    private async Task<bool> PublishAsync()
    {
        try { _ipc.UpdateDsp(); await _ipc.UpdateDeviceCorrectionsAsync(); return true; }
        catch { return false; } // IPC retains its retry state; offline playback must not prevent saving or closing.
    }
    private void NotifyPresetActions()
    {
        OnPropertyChanged(nameof(CanSavePreset));
        OnPropertyChanged(nameof(CanUpdatePreset));
        OnPropertyChanged(nameof(CanApplyPreset));
        OnPropertyChanged(nameof(CanDeletePreset));
        SavePresetCommand.NotifyCanExecuteChanged();
        UpdatePresetCommand.NotifyCanExecuteChanged();
        ApplyPresetCommand.NotifyCanExecuteChanged();
        DeletePresetCommand.NotifyCanExecuteChanged();
    }
    /// <summary>Restore stored control points without overwriting the preset or changing preamp preferences.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyPreset))]
    private async Task ApplyPresetAsync()
    {
        if (!CanApplyPreset) return;
        _points.Clear();
        _points.AddRange(CorrectionCurve.Parse(SelectedPreset!.Points));
        SyncNodes();
        Changed(custom: false);
        // An explicit restore replaces any pending drag and publishes immediately.
        _timer.Stop();
        await FlushAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddPoint()
    {
        if (!CanAdd) return;
        int left = 0;
        for (int i = 1; i < _points.Count - 1; i++)
            if (_points[i + 1].Frequency / _points[i].Frequency > _points[left + 1].Frequency / _points[left].Frequency) left = i;
        double hz = Math.Round(Math.Sqrt(_points[left].Frequency * _points[left + 1].Frequency), 2);
        if (hz - _points[left].Frequency < 0.02 || _points[left + 1].Frequency - hz < 0.02) return;
        _points.Insert(left + 1, new(hz, CorrectionCurve.Evaluate(Points, hz)));
        SelectedNode = left + 1; SyncNodes(); Changed();
    }
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemovePoint() { if (!CanRemove) return; _points.RemoveAt(SelectedNode); SyncNodes(); Changed(); }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ResetFlat() { if (!CanEdit) return; _points.Clear(); _points.AddRange(CorrectionCurve.Parse(CorrectionCurve.Flat)); SyncNodes(); Changed(); }
    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private async Task SavePresetAsync()
    {
        if (!CanSavePreset) return;
        string name = PresetName.Trim();
        if (name.Length == 0 || name.Length > 80 || Presets.Count >= 100 || Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        { ErrorMessage = ToolUtils.GetString("CurvePresetNameError"); return; }
        var preset = new CurvePreset(name, Draft.CurvePoints);
        string? outputId = _editorOutputId;
        bool deviceMode = _deviceMode;
        IsPresetBusy = true;
        try
        {
            await _store.SaveAsync([.. Presets, preset]);
            Presets.Add(preset);
            if (IsEditingTarget(outputId, deviceMode) && Draft.CurvePoints == preset.Points)
            {
                _syncing = true; SelectedPreset = preset; _syncing = false;
                Changed(custom: AppSettings.IsCustomCorrectionDraft);
            }
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
        string? outputId = _editorOutputId;
        bool deviceMode = _deviceMode;
        int index = Presets.IndexOf(selected);
        if (index < 0) return;
        var updated = selected with { Points = CorrectionCurve.Encode(_points) };
        var snapshot = Presets.ToList(); snapshot[index] = updated;
        IsPresetBusy = true;
        try
        {
            await _store.SaveAsync(snapshot);
            bool retainSelection = IsEditingTarget(outputId, deviceMode) && ReferenceEquals(SelectedPreset, selected);
            _syncing = true;
            Presets[index] = updated;
            if (retainSelection) SelectedPreset = updated;
            _syncing = false;
            if (retainSelection) Changed(custom: AppSettings.IsCustomCorrectionDraft);
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
            bool clearSelection = ReferenceEquals(SelectedPreset, selected);
            _syncing = true;
            if (clearSelection) SelectedPreset = null;
            Presets.Remove(selected);
            _syncing = false;
            // Deleting a stored preset leaves the current draft available as a custom curve.
            if (clearSelection) Changed(custom: AppSettings.IsCustomCorrectionDraft);
            ErrorMessage = "";
            OnPropertyChanged(nameof(PresetPlaceholder));
            PresetDeleted?.Invoke();
        }
        catch { ErrorMessage = ToolUtils.GetString("CurvePresetError"); }
        finally { _syncing = false; IsPresetBusy = false; }
    }

    private bool IsEditingTarget(string? outputId, bool deviceMode) => _open && _deviceMode == deviceMode
        && (!deviceMode || string.Equals(_editorOutputId, outputId, StringComparison.OrdinalIgnoreCase));
}
