using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

public sealed partial class ListeningReportPreviewDialog : ContentDialog
{

    public ListeningReportViewModel ViewModel { get; }
    public string ImageDimensions { get; } = $"{ListeningReportImages.Width} × {ListeningReportImages.Height}";

    internal ListeningReportPreviewDialog(ListeningReportViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        CloseButtonText = ToolUtils.GetString("DialogClose");
    }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ViewModel.SaveCommand.Execute(null);
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        args.Cancel = ViewModel.IsSaving;
    }

    internal void ReleasePreview()
    {
        Bindings.StopTracking();
        PosterPreview.Content = null;
    }

    internal async Task<SoftwareBitmap> CaptureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double dpi = XamlRoot.RasterizationScale;
        var surface = new Viewbox
        {
            Width = ListeningReportImages.Width / dpi,
            Height = ListeningReportImages.Height / dpi,
            Child = new ContentPresenter
            {
                Content = ViewModel,
                ContentTemplate = PosterPreview.ContentTemplate
            }
        };
        // 同一模板按导出分辨率单独布局，避免窄窗口先缩小文字和封面再把位图放大。
        Canvas.SetLeft(surface, -surface.Width - XamlRoot.Size.Width);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onLoaded = (_, _) => loaded.TrySetResult();
        surface.Loaded += onLoaded;
        try
        {
            ExportHost.Children.Add(surface);
            await loaded.Task.WaitAsync(token);
            return await ListeningReportImages.CaptureAsync(surface, token);
        }
        finally
        {
            surface.Loaded -= onLoaded;
            ExportHost.Children.Remove(surface);
            surface.Child = null;
        }
    }
}
