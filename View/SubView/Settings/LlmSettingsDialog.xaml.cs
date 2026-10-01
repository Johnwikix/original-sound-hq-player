using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.View.SubView.Settings;

public sealed partial class LlmSettingsDialog : ContentDialog
{
    public LlmSettingsViewModel ViewModel { get; }

    public LlmSettingsDialog(LlmSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            ViewModel.ApiKeyInput = passwordBox.Password;
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        try
        {
            bool saved = await ViewModel.SaveAsync();
            args.Cancel = !saved;
        }
        finally { deferral.Complete(); }
    }
}
