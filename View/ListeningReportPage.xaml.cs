using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

public sealed partial class ListeningReportPage : Page
{
    private readonly AppViewModel _shell = App.Services.GetRequiredService<AppViewModel>();
    private bool _previousBackEnabled;
    internal ListeningReportPreviewDialog? PreviewDialog { get; private set; }

    public ListeningReportViewModel ViewModel { get; } = App.Services.GetRequiredService<ListeningReportViewModel>();

    public ListeningReportPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _previousBackEnabled = _shell.IsBackBtnEnable;
        Frame.Navigated += FrameNavigated;
        ViewModel.Activate();
        UpdateShellBack();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (PreviewDialog is not null) e.Cancel = true;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Frame.Navigated -= FrameNavigated;
        ViewModel.Deactivate();
        _shell.IsBackBtnEnable = _previousBackEnabled;
        base.OnNavigatedFrom(e);
    }

    internal bool CanNavigateBack => Frame?.CanGoBack == true;

    internal void NavigateBack()
    {
        if (PreviewDialog is { } dialog)
        {
            if (!ViewModel.IsSaving) dialog.Hide();
            return;
        }
        if (CanNavigateBack) Frame.GoBack();
    }

    private void FrameNavigated(object sender, NavigationEventArgs e) => UpdateShellBack();

    private void UpdateShellBack() => _shell.IsBackBtnEnable = CanNavigateBack;

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanPreview || PreviewDialog is not null) return;
        var dialog = new ListeningReportPreviewDialog(ViewModel) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        PreviewDialog = dialog;
        try
        {
            ViewModel.BeginPreview(dialog.CaptureAsync);
            await dialog.ShowWithLayoutAsync();
        }
        finally
        {
            ViewModel.EndPreview();
            dialog.ReleasePreview();
            PreviewDialog = null;
        }
    }
}
