using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

public sealed partial class ListeningReportPage : Page
{
    private readonly AppViewModel _shell = App.Services.GetRequiredService<AppViewModel>();
    private bool _previousBackEnabled;
    private bool _openingPreview;
    private bool _restoreScroll;

    public ListeningReportViewModel ViewModel { get; } = App.Services.GetRequiredService<ListeningReportViewModel>();

    public ListeningReportPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _previousBackEnabled = _shell.IsBackBtnEnable;
        _restoreScroll = e.NavigationMode == NavigationMode.Back;
        Frame.Navigated += FrameNavigated;
        ViewModel.Activate();
        UpdateShellBack();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Frame.Navigated -= FrameNavigated;
        if (!_openingPreview) ViewModel.Deactivate();
        _shell.IsBackBtnEnable = _previousBackEnabled;
        base.OnNavigatedFrom(e);
    }

    internal bool CanNavigateBack => Frame?.CanGoBack == true;

    internal void NavigateBack()
    {
        if (CanNavigateBack) Frame.GoBack();
    }

    private void FrameNavigated(object sender, NavigationEventArgs e) => UpdateShellBack();

    private void UpdateShellBack() => _shell.IsBackBtnEnable = CanNavigateBack;

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanPreview || _openingPreview) return;
        ViewModel.ReportScrollOffset = ReportScroll.VerticalOffset;
        _openingPreview = true;
        try { Frame.Navigate(typeof(ListeningReportPreviewPage), null, ListeningReportPreviewPage.CreateTransition()); }
        finally { _openingPreview = false; }
    }

    private void Report_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_restoreScroll) return;
        _restoreScroll = false;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            ReportScroll.UpdateLayout();
            ReportScroll.ChangeView(null, ViewModel.ReportScrollOffset, null, disableAnimation: true);
            // Programmatic focus can scroll the button into view, overriding the restored position.
            Focus(FocusState.Programmatic);
        });
    }
}
