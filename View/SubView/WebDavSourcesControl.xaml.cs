using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
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
    private bool _editing;
    public WebDavSourcesViewModel ViewModel { get; }
    public WebDavSourcesControl()
    {
        ViewModel = App.Services.GetRequiredService<WebDavSourcesViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
    public Task ShowAddAsync(RemoteSourceKind kind) => EditAsync(null, kind);
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WebDavSourceItem item) await EditAsync(item.Source, item.Source.Kind);
    }
    private async Task EditAsync(WebDavSource? source, RemoteSourceKind kind)
    {
        if (_editing) return;
        _editing = true;
        try
        {
            using var form = new WebDavConnectionViewModel(App.Services.GetRequiredService<WebDavTransport>(),
                App.Services.GetRequiredService<WebDavLibraryService>(), source, kind);
            await new WebDavConnectionDialog(form, ViewModel.TryQueueSourceSave).ShowThemedAsync(XamlRoot);
        }
        finally { _editing = false; }
    }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not WebDavSourceItem item) return;
        if (item.Source.Kind == RemoteSourceKind.WebDav)
            await new WebDavBrowserDialog(item.Source).ShowThemedAsync(XamlRoot);
        else
        {
            ViewModel.ShowSongs(item.Source);
            App.Services.GetRequiredService<LibraryNavigationService>().ShowSongs();
        }
    }
}
