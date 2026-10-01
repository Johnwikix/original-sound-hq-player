using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView.Settings
{
    public sealed partial class LyricsSettingsControl : UserControl
    {
        public SettingsViewModel ViewModel { get; }
        private bool _llmDialogOpen;

        public LyricsSettingsControl()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            DataContext = this;
        }

        private async void ConfigureLlm_Click(object sender, RoutedEventArgs e)
        {
            if (_llmDialogOpen) return;
            _llmDialogOpen = true;
            WinUIMusicPlayer.ViewModel.Controls.LlmSettingsViewModel? dialogViewModel = null;
            try
            {
                dialogViewModel = App.Services.GetRequiredService<WinUIMusicPlayer.ViewModel.Controls.LlmSettingsViewModel>();
                var dialog = new LlmSettingsDialog(dialogViewModel)
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = ActualTheme
                };
                await dialogViewModel.LoadAsync();
                await dialog.ShowAsync();
            }
            finally
            {
                dialogViewModel?.Dispose();
                _llmDialogOpen = false;
            }
        }
    }
}
