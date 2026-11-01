using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel.Controls;

namespace AppearanceUiRegression;

public sealed partial class PcmControl : UserControl
{
    public DspSettingsViewModel ViewModel { get; }
    public PcmControl(DspSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
