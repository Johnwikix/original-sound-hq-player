using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class WebDavConnectionDialog : ContentDialog
{
    public WebDavConnectionViewModel ViewModel { get; }
    public WebDavConnectionDialog(WebDavConnectionViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Closed += (_, _) => ViewModel.Dispose();
    }
    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { args.Cancel = !await ViewModel.SaveAsync(); }
        finally { deferral.Complete(); }
    }
}
