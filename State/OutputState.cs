using CommunityToolkit.Mvvm.ComponentModel;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.State;

/// <summary>后端确认的输出展示状态，与用户请求的音频偏好分开。</summary>
public sealed class OutputState : ObservableObject
{
    public string AtmosStatusText { get; internal set => SetProperty(ref field, value); } = ToolUtils.GetString("AtmosWaiting");
    public string SurroundStatusText { get; internal set => SetProperty(ref field, value); } = ToolUtils.GetString("SurroundWaiting");
}
