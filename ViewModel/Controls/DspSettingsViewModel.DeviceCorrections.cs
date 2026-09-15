using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel.Controls;

public partial class DspSettingsViewModel
{
    public ObservableCollection<BassOutputDevice> CorrectionDevices { get; } = [];
    private string? _actualCorrectionDeviceId;
    private Func<DspSettings>? _correctionDraft;
    private Action<DspSettings>? _loadCorrectionDraft;
    private Action? _refreshCorrectionDraft;
    public string CorrectionError { get => field; private set => SetProperty(ref field, value); } = "";
    public bool CorrectionFailed
    {
        get => field;
        private set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(ShowRetryCorrection)); }
    }
    private bool _correctionApplyError;

    private void OnCorrectionSyncChanged() => _queue.TryEnqueue(RefreshCorrectionSyncState);

    private void RefreshCorrectionSyncState()
    {
        if (!_loaded || CorrectionBusy) return;
        if (_ipc.CorrectionSyncFailed && _pendingCorrection == null)
        {
            CorrectionError = ToolUtils.GetString("DspDeviceApplyError");
            CorrectionFailed = true;
            _correctionApplyError = true;
        }
        else if (!_ipc.CorrectionSyncFailed && _correctionApplyError
            && (_pendingCorrection == null || ReferenceEquals(_pendingCorrection, AppSettings.DeviceCorrections)))
        {
            _pendingCorrection = null;
            CorrectionFailed = false;
            _correctionApplyError = false;
        }
        OnPropertyChanged(nameof(CanRetryCorrection));
        OnPropertyChanged(nameof(ShowRetryCorrection));
        RetryCorrectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>绑定命令读取编辑器当前曲线。</summary>
    public void BeginCorrectionEditing(Func<DspSettings> draft, Action<DspSettings> load, Action? refresh = null)
    {
        _correctionDraft = draft;
        _loadCorrectionDraft = load;
        _refreshCorrectionDraft = refresh;
        RefreshCorrectionState();
    }

    public void EndCorrectionEditing()
    {
        _correctionDraft = null;
        _loadCorrectionDraft = null;
        _refreshCorrectionDraft = null;
        RefreshCorrectionState();
    }

    /// <summary>空端点选项表示正在使用的实际输出；保存时始终转换为具体稳定 ID。</summary>
    public BassOutputDevice? SelectedCorrectionDevice
    {
        get => field;
        set { if (SetProperty(ref field, value)) RefreshCorrectionState(); }
    }

    public bool DeviceCorrectionEnabled
    {
        get => AppSettings.DeviceCorrections.Enabled;
        set
        {
            if (value == DeviceCorrectionEnabled || CorrectionBusy) return;
            _ = SaveCorrectionsAsync(AppSettings.DeviceCorrections with { Enabled = value });
        }
    }

    public string ActualCorrectionOutput { get => field; private set => SetProperty(ref field, value); } = "";
    public string CorrectionBindingStatus { get => field; private set => SetProperty(ref field, value); } = "";
    public string SaveCorrectionLabel { get => field; private set => SetProperty(ref field, value); } = "";
    public bool CanChangeCorrectionMode => !CorrectionBusy;
    public bool CorrectionBusy { get => field; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(CanChangeCorrectionMode)); OnPropertyChanged(nameof(CanRetryCorrection)); RetryCorrectionCommand.NotifyCanExecuteChanged(); RefreshCorrectionState(); } } }

    private string? CorrectionDeviceId => string.IsNullOrEmpty(SelectedCorrectionDevice?.EndpointId)
        ? _actualCorrectionDeviceId : SelectedCorrectionDevice.EndpointId;
    private bool CanBindCorrection() => !CorrectionBusy && !string.IsNullOrEmpty(CorrectionDeviceId)
        && (AppSettings.DeviceCorrections.Find(CorrectionDeviceId) != null || AppSettings.DeviceCorrections.Bindings.Length < 64);
    private bool HasCorrection() => !CorrectionBusy && AppSettings.DeviceCorrections.Find(CorrectionDeviceId) != null;

    private bool CanLoadCorrection() => HasCorrection() && (_loadCorrectionDraft == null
        || CorrectionCurve.UsesCurve(AppSettings.DeviceCorrections.Find(CorrectionDeviceId)!.Settings));

    private string CorrectionDeviceName(string? id)
    {
        foreach (var device in CorrectionDevices)
            if (!string.IsNullOrEmpty(id) && string.Equals(device.EndpointId, id, StringComparison.OrdinalIgnoreCase)) return device.Name;
        return AppSettings.DeviceCorrections.Find(id)?.DeviceName ?? id ?? "";
    }

    private void RefreshCorrectionState()
    {
        string? id = CorrectionDeviceId;
        var binding = AppSettings.DeviceCorrections.Find(id);
        ActualCorrectionOutput = string.IsNullOrEmpty(_actualCorrectionDeviceId)
            ? ToolUtils.GetString("DspDeviceNoOutput")
            : string.Format(ToolUtils.GetString("DspDeviceActual"), CorrectionDeviceName(_actualCorrectionDeviceId));
        CorrectionBindingStatus = string.IsNullOrEmpty(id) ? ToolUtils.GetString("DspDeviceNoOutput")
            : binding == null ? ToolUtils.GetString("DspDeviceUnbound")
            : string.Format(ToolUtils.GetString("DspDeviceBound"), binding.DeviceName);
        SaveCorrectionLabel = ToolUtils.GetString(binding == null ? "DspDeviceBind" : "DspDeviceUpdate");
        BindCorrectionCommand.NotifyCanExecuteChanged();
        LoadCorrectionCommand.NotifyCanExecuteChanged();
        RemoveCorrectionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RefreshCorrectionDevicesAsync()
    {
        if (CorrectionBusy) return;
        CorrectionBusy = true;
        string? selected = SelectedCorrectionDevice?.EndpointId;
        try
        {
            var wasapi = await _ipc.GetWasapiDevices();
            var asio = await _ipc.GetAsioDevices();
            CorrectionDevices.Clear();
            CorrectionDevices.Add(new() { Name = ToolUtils.GetString("DspDeviceCurrent"), EndpointId = "" });
            foreach (var device in wasapi)
                if (_ipc.GetWasapiEndpointId(device.id) is { Length: > 0 } id)
                    CorrectionDevices.Add(new() { Name = device.name, EndpointId = id });
            foreach (var device in asio)
                if (_ipc.GetAsioEndpointId(device.id) is { Length: > 0 } id)
                    CorrectionDevices.Add(new() { Name = "ASIO · " + device.name, EndpointId = id });
            foreach (var binding in AppSettings.DeviceCorrections.Bindings)
                if (!CorrectionDevices.Any(d => string.Equals(d.EndpointId, binding.DeviceId, StringComparison.OrdinalIgnoreCase)))
                    CorrectionDevices.Add(new() { Name = binding.DeviceName, EndpointId = binding.DeviceId });
            SelectedCorrectionDevice = CorrectionDevices.FirstOrDefault(d => d.EndpointId == selected) ?? CorrectionDevices[0];
        }
        catch (Exception ex)
        {
            App.GetLogger<DspSettingsViewModel>().LogWarning(ex, "Correction device enumeration failed");
            ShowCorrectionError();
        }
        finally { CorrectionBusy = false; RefreshCorrectionSyncState(); }
    }

    /// <summary>显式保存编辑器快照；以后修改草稿不会隐式覆盖其他设备。</summary>
    [RelayCommand(CanExecute = nameof(CanBindCorrection))]
    private async Task BindCorrectionAsync()
    {
        string? id = CorrectionDeviceId;
        if (string.IsNullOrEmpty(id)) return;
        var bindings = AppSettings.DeviceCorrections.Bindings.ToList();
        var state = _ipc.CurrentDspState?.State;
        var current = _correctionDraft?.Invoke() ?? AppSettings.ResolveResponseSettings(state?.OutputDeviceId, state?.OutputGeneration ?? 0);
        var snapshot = new DeviceCorrection { DeviceId = id, DeviceName = CorrectionDeviceName(id), Settings = current.ToUnifiedGain() };
        int index = bindings.FindIndex(b => string.Equals(b.DeviceId, id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) bindings[index] = snapshot;
        else bindings.Add(snapshot);
        await SaveCorrectionsAsync(AppSettings.DeviceCorrections with { Bindings = bindings.ToArray() });
    }

    [RelayCommand(CanExecute = nameof(CanLoadCorrection))]
    private async Task LoadCorrectionAsync()
    {
        if (AppSettings.DeviceCorrections.Find(CorrectionDeviceId) is not { } binding) return;
        if (_loadCorrectionDraft != null)
        {
            _loadCorrectionDraft(binding.Settings);
            return;
        }
        var settings = binding.Apply(AppSettings.Dsp.ToUnifiedGain());
        if (AppSettings.DeviceCorrections.Enabled && _ipc.CurrentDspState?.State is { OutputDeviceId.Length: > 0 } state)
            AppSettings.SetLiveCorrection(state.OutputDeviceId, state.OutputGeneration, settings);
        else
            AppSettings.Dsp = settings;
        LoadValues();
        _dirty = true;
        await CommitAsync();
    }

    [RelayCommand(CanExecute = nameof(HasCorrection))]
    private async Task RemoveCorrectionAsync()
    {
        string? id = CorrectionDeviceId;
        var candidate = AppSettings.DeviceCorrections with
        {
            Bindings = AppSettings.DeviceCorrections.Bindings.Where(b => !string.Equals(b.DeviceId, id, StringComparison.OrdinalIgnoreCase)).ToArray()
        };
        await SaveCorrectionsAsync(candidate);
    }

    private DeviceCorrections? _pendingCorrection;
    public bool CanRetryCorrection => !CorrectionBusy && (_pendingCorrection != null || _ipc.CorrectionSyncFailed);
    public bool ShowRetryCorrection => CorrectionFailed && (_pendingCorrection != null || _ipc.CorrectionSyncFailed);

    [RelayCommand(CanExecute = nameof(CanRetryCorrection))]
    private Task RetryCorrectionAsync() => SaveCorrectionsAsync(AppSettings.DeviceCorrections);

    private async Task SaveCorrectionsAsync(DeviceCorrections candidate)
    {
        if (CorrectionBusy) return;
        CorrectionBusy = true;
        _pendingCorrection = candidate;
        bool saved = false;
        try
        {
            CorrectionFailed = false;
            _correctionApplyError = false;
            if (!ReferenceEquals(candidate, AppSettings.DeviceCorrections))
                await _database.SaveDeviceCorrectionsAsync(candidate);
            else
                await _database.SaveCurrentDeviceCorrectionsAsync();
            saved = true;
            _pendingCorrection = AppSettings.DeviceCorrections;
            _refreshCorrectionDraft?.Invoke();
            await _ipc.UpdateDeviceCorrectionsAsync();
            _pendingCorrection = null;
        }
        catch (Exception ex)
        {
            _pendingCorrection = AppSettings.DeviceCorrections;
            _correctionApplyError = saved;
            App.GetLogger<DspSettingsViewModel>().LogWarning(ex,
                "Correction {Operation} failed for device {DeviceId}; binding count {BindingCount}",
                _correctionApplyError ? "synchronization" : "save", CorrectionDeviceId, candidate.Bindings.Length);
            CorrectionError = ToolUtils.GetString(_correctionApplyError
                ? "DspDeviceApplyError" : "DspDeviceSaveError");
            CorrectionFailed = true;
        }
        finally
        {
            CorrectionBusy = false;
            OnPropertyChanged(nameof(DeviceCorrectionEnabled));
            OnPropertyChanged(nameof(CanRetryCorrection));
            OnPropertyChanged(nameof(ShowRetryCorrection));
        }
    }

    private void ShowCorrectionError()
    {
        _correctionApplyError = false;
        CorrectionError = ToolUtils.GetString("DspDeviceError");
        CorrectionFailed = true;
    }
}
