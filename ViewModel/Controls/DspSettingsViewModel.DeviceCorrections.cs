using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.Input;
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
            if (value == DeviceCorrectionEnabled) return;
            AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Enabled = value };
            OnPropertyChanged();
            RefreshCorrectionState();
            _ = SaveCorrectionsAsync();
        }
    }

    public string ActualCorrectionOutput { get => field; private set => SetProperty(ref field, value); } = "";
    public string CorrectionBindingStatus { get => field; private set => SetProperty(ref field, value); } = "";
    public string SaveCorrectionLabel { get => field; private set => SetProperty(ref field, value); } = "";
    public bool CorrectionBusy { get => field; private set { if (SetProperty(ref field, value)) RefreshCorrectionState(); } }

    private string? CorrectionDeviceId => string.IsNullOrEmpty(SelectedCorrectionDevice?.EndpointId)
        ? _actualCorrectionDeviceId : SelectedCorrectionDevice.EndpointId;
    private bool CanBindCorrection() => !CorrectionBusy && !string.IsNullOrEmpty(CorrectionDeviceId)
        && (AppSettings.DeviceCorrections.Find(CorrectionDeviceId) != null || AppSettings.DeviceCorrections.Bindings.Length < 64);
    private bool HasCorrection() => !CorrectionBusy && AppSettings.DeviceCorrections.Find(CorrectionDeviceId) != null;

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
        catch (Exception) { ShowCorrectionError(); }
        finally { CorrectionBusy = false; }
    }

    /// <summary>显式保存编辑器快照；以后修改草稿不会隐式覆盖其他设备。</summary>
    [RelayCommand(CanExecute = nameof(CanBindCorrection))]
    private async Task BindCorrectionAsync()
    {
        string? id = CorrectionDeviceId;
        if (string.IsNullOrEmpty(id)) return;
        var bindings = AppSettings.DeviceCorrections.Bindings.ToList();
        var snapshot = new DeviceCorrection { DeviceId = id, DeviceName = CorrectionDeviceName(id), Settings = AppSettings.Dsp.ToUnifiedGain() };
        int index = bindings.FindIndex(b => string.Equals(b.DeviceId, id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) bindings[index] = snapshot;
        else bindings.Add(snapshot);
        AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with { Bindings = bindings.ToArray() };
        await SaveCorrectionsAsync();
    }

    [RelayCommand(CanExecute = nameof(HasCorrection))]
    private async Task LoadCorrectionAsync()
    {
        if (AppSettings.DeviceCorrections.Find(CorrectionDeviceId) is not { } binding) return;
        AppSettings.Dsp = binding.Apply(AppSettings.Dsp.ToUnifiedGain());
        LoadValues();
        _dirty = true;
        await CommitAsync();
    }

    [RelayCommand(CanExecute = nameof(HasCorrection))]
    private async Task RemoveCorrectionAsync()
    {
        string? id = CorrectionDeviceId;
        AppSettings.DeviceCorrections = AppSettings.DeviceCorrections with
        {
            Bindings = AppSettings.DeviceCorrections.Bindings.Where(b => !string.Equals(b.DeviceId, id, StringComparison.OrdinalIgnoreCase)).ToArray()
        };
        await SaveCorrectionsAsync();
    }

    private async Task SaveCorrectionsAsync()
    {
        try
        {
            ImportFailed = false;
            _ipc.UpdateDeviceCorrections();
            await _database.SaveSettingAsync();
        }
        catch (Exception) { ShowCorrectionError(); }
        RefreshCorrectionState();
    }

    private void ShowCorrectionError()
    {
        ImportError = ToolUtils.GetString("DspDeviceError");
        ImportFailed = true;
    }
}
