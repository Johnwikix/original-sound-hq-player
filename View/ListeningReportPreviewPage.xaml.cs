using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

public sealed partial class ListeningReportPreviewPage : Page
{
    private readonly AppViewModel _shell = App.Services.GetRequiredService<AppViewModel>();
    private bool _previousBackEnabled;
    private bool _returningToReport;

    public ListeningReportViewModel ViewModel { get; } = App.Services.GetRequiredService<ListeningReportViewModel>();

    public ListeningReportPreviewPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _previousBackEnabled = _shell.IsBackBtnEnable;
        Frame.Navigated += FrameNavigated;
        ViewModel.PropertyChanged += PreviewStateChanged;
        ViewModel.BeginPreview(CaptureAsync);
        UpdateShellBack();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (ViewModel.IsCapturing || (e.NavigationMode == NavigationMode.Back && ViewModel.IsSaving)) e.Cancel = true;
        _returningToReport = !e.Cancel && e.NavigationMode == NavigationMode.Back &&
            e.SourcePageType == typeof(ListeningReportPage);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Frame.Navigated -= FrameNavigated;
        ViewModel.PropertyChanged -= PreviewStateChanged;
        if (_returningToReport) ViewModel.EndPreview();
        else ViewModel.Deactivate();
        _shell.IsBackBtnEnable = _previousBackEnabled;
        base.OnNavigatedFrom(e);
    }

    internal bool CanNavigateBack => !ViewModel.IsSaving && Frame?.CanGoBack == true;

    internal void NavigateBack()
    {
        if (CanNavigateBack) Frame.GoBack(CreateTransition());
    }

    internal static NavigationTransitionInfo CreateTransition()
        => new UISettings().AnimationsEnabled
            ? new DrillInNavigationTransitionInfo()
            : new SuppressNavigationTransitionInfo();

    private void PreviewStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsSaving)) UpdateShellBack();
    }

    private void FrameNavigated(object sender, NavigationEventArgs e) => UpdateShellBack();

    private void UpdateShellBack() => _shell.IsBackBtnEnable = CanNavigateBack;

    internal Task<SoftwareBitmap> CaptureAsync(CancellationToken token) => ListeningReportImages.CaptureAsync(PosterCanvas, token);
}
