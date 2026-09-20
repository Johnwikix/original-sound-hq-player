using CommunityToolkit.Mvvm.ComponentModel;
namespace WinUIMusicPlayer.State;

/// <summary>跨页面窗口展示状态；原生窗口操作由 ShellService 执行。</summary>
public sealed class ShellState : ObservableObject
{
    public string InfoBarTitle { get; set => SetProperty(ref field, value); } = string.Empty;
    public string InfoBarMessage { get; set => SetProperty(ref field, value); } = string.Empty;
    public bool InfoBarIsOpen { get; set => SetProperty(ref field, value); } = false;
    public bool IsColorPickerVisible { get; set => SetProperty(ref field, value); }
    public bool IsFullScreen { get; set => SetProperty(ref field, value); }
    public bool IsMaximized { get; set => SetProperty(ref field, value); }
    public bool IsPointerOverTitleBar { get; set => SetProperty(ref field, value); } = true;
}
