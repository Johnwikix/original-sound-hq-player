using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class WebDavBrowserDialog : ContentDialog
{
    public WebDavBrowserViewModel ViewModel { get; }
    public WebDavBrowserDialog(WebDavSource source)
    {
        ViewModel = new(source, App.Services.GetRequiredService<WebDavLibraryService>(), App.Services.GetRequiredService<WebDavTransport>(),
            App.Services.GetRequiredService<MusicDatabaseService>(), App.Services.GetRequiredService<PlaybackCoordinator>());
        InitializeComponent();
        Opened += async (_, _) => await ViewModel.LoadAsync();
        Closed += (_, _) => ViewModel.Dispose();
    }
    private async void Item_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WebDavEntry entry) await ViewModel.OpenAsync(entry);
    }
}
