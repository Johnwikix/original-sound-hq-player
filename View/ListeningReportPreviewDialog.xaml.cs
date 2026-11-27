using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
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
        if (VisualTreeHelper.GetChildrenCount(PosterPreview) == 0 ||
            VisualTreeHelper.GetChild(PosterPreview, 0) is not Grid { ActualWidth: > 0, ActualHeight: > 0 } poster)
            throw new InvalidOperationException("Report poster must be measured before export.");

        var scene = ListeningReportPoster.Read(poster);
        var images = ViewModel.CopyPosterImages();
        using var cover = images.Cover;
        using var background = images.Background;
        return await Task.Run(() => scene.RenderAsync(cover, background, token), token);
    }
}
