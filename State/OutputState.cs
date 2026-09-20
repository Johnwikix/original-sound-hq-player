using CommunityToolkit.Mvvm.ComponentModel;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.Model;
using System.Collections.ObjectModel;

namespace WinUIMusicPlayer.State;

/// <summary>后端确认的输出展示状态，与用户请求的音频偏好分开。</summary>
public sealed class OutputState : ObservableObject
{
    public bool IsRealDeviceChange { get; set; } = true;
    public bool IsLoadingDevices { get; internal set; }
    public ObservableCollection<BassOutputDevice> BassOutputDevices { get; set => SetProperty(ref field, value); } = new();
    public BassOutputDevice SelectedDevice { get; set => SetProperty(ref field, value); } = null!;
    public ObservableCollection<BassOutputDevice> AtmosDevices { get; } = new();
    internal void NotifyAtmosDevicesChanged() => OnPropertyChanged("SelectedAtmosDevice");
    public string AtmosStatusText { get; internal set => SetProperty(ref field, value); } = ToolUtils.GetString("AtmosWaiting");
    public string SurroundStatusText { get; internal set => SetProperty(ref field, value); } = ToolUtils.GetString("SurroundWaiting");
}
