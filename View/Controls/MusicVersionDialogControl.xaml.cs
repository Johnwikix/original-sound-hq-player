using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>展示候选／合并说明，输入状态由 ViewModel 拥有。</summary>
public sealed partial class MusicVersionDialogControl : UserControl
{
    /// <summary>初始化指定预览。</summary>
    public MusicVersionDialogControl(MusicVersionDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
    /// <summary>获取此次对话框的输入模型。</summary>
    public MusicVersionDialogViewModel ViewModel { get; }
    /// <summary>转换预览区域可见性。</summary>
    public Visibility VisibilityFor(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
