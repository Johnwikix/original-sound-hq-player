using Microsoft.Extensions.DependencyInjection;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class WebDavSourcesControl : UserControl
{
    public WebDavSourcesViewModel ViewModel { get; }
    public WebDavSourcesControl()
    {
        ViewModel = App.Services.GetRequiredService<WebDavSourcesViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
    public System.Threading.Tasks.Task ShowAddAsync() => EditAsync(null);
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WebDavSourceItem item) await EditAsync(item.Source);
    }
    private async System.Threading.Tasks.Task EditAsync(WebDavSource? source)
    {
        var form = new WebDavConnectionViewModel(App.Services.GetRequiredService<WebDavTransport>(), App.Services.GetRequiredService<WebDavLibraryService>(), source);
        await new WebDavConnectionDialog(form).ShowThemedAsync(XamlRoot);
    }
    private void Songs_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not WebDavSourceItem item) return;
        ViewModel.ShowSongs(item.Source);
        App.Services.GetRequiredService<MainPage>().NavigateToMusicBrowsePage();
        App.Services.GetRequiredService<MusicBrowsePage>().SelectBarItem("song");
    }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is WebDavSourceItem item)
            await new WebDavBrowserDialog(item.Source).ShowThemedAsync(XamlRoot);
    }
}
