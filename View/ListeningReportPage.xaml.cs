using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

public sealed partial class ListeningReportPage : Page
{
    public ListeningReportViewModel ViewModel { get; } = App.Services.GetRequiredService<ListeningReportViewModel>();

    public ListeningReportPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate(CaptureAsync, PickPathAsync);
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (ViewModel.IsCapturing) e.Cancel = true;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    internal void NavigateBack()
    {
        if (ViewModel.IsPreview)
        {
            if (ViewModel.ClosePreviewCommand.CanExecute(null)) ViewModel.ClosePreviewCommand.Execute(null);
        }
        else if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavigateBack();

    internal Task<SoftwareBitmap> CaptureAsync(CancellationToken token) => ListeningReportImages.CaptureAsync(PosterCanvas, token);

    private static Task<string?> PickPathAsync(string suggestedName, CancellationToken token)
        => ListeningReportImages.PickPathAsync(App.MainWindow.AppWindow.Id, suggestedName, token);
}
