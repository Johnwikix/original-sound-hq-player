using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView.Settings;

public sealed partial class WebDavCacheControl : UserControl
{
    public WebDavSourcesViewModel ViewModel { get; }
    public WebDavCacheControl()
    {
        ViewModel = App.Services.GetRequiredService<WebDavSourcesViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}
