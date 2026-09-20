using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using ZLinq;

namespace WinUIMusicPlayer.Services;

/// <summary>输出设备枚举与选择适配。停止后拒绝迟到枚举结果，等待实际枚举完成再释放 IPC。</summary>
public sealed class OutputDeviceService(AppState state, MusicDatabaseService database) : IDisposable
{
    private Task _refresh = Task.CompletedTask;
    private bool _started, _stopped;
    public void Start()
    {
        if (_started || _stopped) return;
        _started = true;
        state.Output.PropertyChanged += Changed;
    }
    public Task RefreshAsync()
    {
        if (_stopped || state.Lifecycle.Phase == AppPhase.Stopping) return Task.CompletedTask;
        Start();
        return _refresh.IsCompleted ? _refresh = GetWasapiDeviceAsync() : _refresh;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_stopped || e.PropertyName != nameof(OutputState.SelectedDevice)) return;
        var value = state.Output.SelectedDevice;
        if (value is not null)
        {
            if (state.Output.IsRealDeviceChange)
            {
                if (state.Lifecycle.IsReady)
                {
                    if (value.OutputMode != "ASIO")
                    {
                        AppSettings.BassOutputDeviceId = value.Id;
                        AppSettings.WasapiEndpointId = value.EndpointId;
                    }
                    else
                    {
                        AppSettings.BassASIODeviceId = value.AsioId;
                    }
                    AppSettings.DeviceName = value.Name;
                    AppSettings.OutputMode = value.OutputMode;
                    _ = database.SaveSettingAsync();
                    AppSettings.OnOutputSettingsChanged();
                }
            }
            else
            {
                state.Output.IsRealDeviceChange = true;
            }
        }
    }
    private async Task GetWasapiDeviceAsync()
    {
        if (state.Output.IsLoadingDevices) return;
        state.Output.IsLoadingDevices = true;
        try
        {
            state.Output.BassOutputDevices.Clear();
            //默认设备
            state.Output.BassOutputDevices.Add(new BassOutputDevice
            {
                Name = "DefaultDevice",
                Tag = ToolUtils.GetString("DefaultDevice") + " [DirectSound]",
                Id = -1,
                OutputMode = "DirectSound"
            });
            state.Output.BassOutputDevices.Add(new BassOutputDevice
            {
                Name = "DefaultDevice",
                Tag = $"{ToolUtils.GetString("DefaultDevice")} [{ToolUtils.GetString("WasapiSharedText")}]",
                Id = -1,
                OutputMode = "WasapiShared"
            });
            state.Output.BassOutputDevices.Add(new BassOutputDevice
            {
                Name = "DefaultDevice",
                Tag = $"{ToolUtils.GetString("DefaultDevice")} [{ToolUtils.GetString("WasapiExclusivePushText")}]",
                Id = -1,
                OutputMode = "WasapiExclusivePush"
            });
            state.Output.BassOutputDevices.Add(new BassOutputDevice
            {
                Name = "DefaultDevice",
                Tag = $"{ToolUtils.GetString("DefaultDevice")} [{ToolUtils.GetString("WasapiExclusiveEventText")}]",
                Id = -1,
                OutputMode = "WasapiExclusiveEvent"
            });

            var cmd = App.Services.GetRequiredService<BassPlayerCommandService>();

            // ASIO devices from server
            var asioDevices = await cmd.GetAsioDevices();
            if (_stopped || state.Lifecycle.Phase == AppPhase.Stopping) return;
            foreach (var (id, name) in asioDevices)
            {
                state.Output.BassOutputDevices.Add(new BassOutputDevice
                {
                    Name = name,
                    Tag = name + " [ASIO]",
                    AsioId = id,
                    OutputMode = "ASIO"
                });
            }

            // WASAPI devices from server
            var wasapiDevices = await cmd.GetWasapiDevices();
            if (_stopped || state.Lifecycle.Phase == AppPhase.Stopping) return;
            foreach (var (id, name) in wasapiDevices)
            {
                if (!state.Output.BassOutputDevices.AsValueEnumerable().Any(d => d.EndpointId == cmd.GetWasapiEndpointId(id) && d.OutputMode == "WasapiShared"))
                {
                    state.Output.BassOutputDevices.Add(new BassOutputDevice
                    {
                        Name = name,
                        Tag = $"{name} [{ToolUtils.GetString("WasapiSharedText")}]",
                        Id = id,
                        EndpointId = cmd.GetWasapiEndpointId(id),
                        OutputMode = "WasapiShared"
                    });
                    state.Output.BassOutputDevices.Add(new BassOutputDevice
                    {
                        Name = name,
                        Tag = $"{name} [{ToolUtils.GetString("WasapiExclusivePushText")}]",
                        Id = id,
                        EndpointId = cmd.GetWasapiEndpointId(id),
                        OutputMode = "WasapiExclusivePush"
                    });
                    state.Output.BassOutputDevices.Add(new BassOutputDevice
                    {
                        Name = name,
                        Tag = $"{name} [{ToolUtils.GetString("WasapiExclusiveEventText")}]",
                        Id = id,
                        EndpointId = cmd.GetWasapiEndpointId(id),
                        OutputMode = "WasapiExclusiveEvent"
                    });
                }
            }

            var device = state.Output.BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.OutputMode == AppSettings.OutputMode && (string.IsNullOrEmpty(AppSettings.WasapiEndpointId)
                || d.OutputMode == "ASIO" || d.OutputMode == "DirectSound" ? d.Name == AppSettings.DeviceName : d.EndpointId == AppSettings.WasapiEndpointId));
            if (device is null)
            {
                // 枚举不到已保存设备（未上电/驱动未就绪等瞬时原因）时只回退内存状态到默认设备，
                // 不触发落盘，避免把用户保存的输出设备设置永久重置（下次启动设备在位时自动恢复）
                AppSettings.OutputMode = "DirectSound";
                AppSettings.BassOutputDeviceId = -1;
                AppSettings.WasapiEndpointId = null;
                AppSettings.DeviceName = "DefaultDevice";
                state.Output.IsRealDeviceChange = false;
                state.Output.SelectedDevice = state.Output.BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == "DefaultDevice" && d.OutputMode == "DirectSound");
                AppSettings.OnOutputSettingsChanged();
            }
            else
            {
                // 启动/刷新枚举时回选已保存设备不算真实切换：跳过落盘（避免多余全量写盘），
                // 但保留输出重配事件以维持原有启动初始化行为
                state.Output.IsRealDeviceChange = false;
                state.Output.SelectedDevice = device;
                if (device.OutputMode.StartsWith("Wasapi", StringComparison.Ordinal))
                {
                    AppSettings.BassOutputDeviceId = device.Id;
                    AppSettings.WasapiEndpointId = device.EndpointId;
                }
            }
        }
        finally
        {
            if (!_stopped && state.Lifecycle.Phase != AppPhase.Stopping) RefreshAtmosDevices();
            state.Output.IsLoadingDevices = false;
        }
    }

    private void RefreshAtmosDevices()
    {
        state.Output.AtmosDevices.Clear();
        state.Output.AtmosDevices.Add(new BassOutputDevice { EndpointId = "", Name = ToolUtils.GetString("AtmosCurrentDevice") });
        bool found = string.IsNullOrEmpty(state.Preferences.Audio.AtmosEndpointId);
        foreach (var device in state.Output.BassOutputDevices)
        {
            if (device.OutputMode != "WasapiShared" || string.IsNullOrEmpty(device.EndpointId)) continue;
            state.Output.AtmosDevices.Add(device);
            found |= string.Equals(device.EndpointId, state.Preferences.Audio.AtmosEndpointId, StringComparison.OrdinalIgnoreCase);
        }
        if (!found)
            state.Output.AtmosDevices.Add(new BassOutputDevice { EndpointId = state.Preferences.Audio.AtmosEndpointId, Name = ToolUtils.GetString("AtmosSavedDeviceUnavailable") });
        state.Output.NotifyAtmosDevicesChanged();
    }


    public async Task StopAsync()
    {
        Dispose();
        await _refresh;
    }
    public void Dispose()
    {
        _stopped = true;
        state.Output.PropertyChanged -= Changed;
    }
}
