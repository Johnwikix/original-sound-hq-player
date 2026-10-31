using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class HomePolishWindow
{
    private readonly List<WeakReference<MusicCoverControl>> _retiredCovers = [];
    private async Task CheckCoverCancellation()
    {
        string oldCache = AppSettings.MusicCoverCache;
        string cache = Path.Combine(_output, "cancel-cache");
        Directory.CreateDirectory(cache);
        var music = new Music { Id = 992231, Title = "cancelled decode", ImageHash = "cancelled-native" };
        ToolUtils.PrepareArtwork(music.Id);
        var bytes = await ToolUtils.GetRawImage(music);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        AppSettings.MusicCoverCache = cache;
        // Only this fixture's dedicated cache file is reset, allowing repeat runs with the same output name.
        string isolatedFile = CoverLoadQueue.GetThumbCachePath(music.ImageHash, 150);
        if (File.Exists(isolatedFile)) File.Delete(isolatedFile);
        bool cancelled = false;
        try
        {
            var method = typeof(CoverLoadQueue).GetMethod("DecodeAndCacheThumbAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            try { await (Task)method.Invoke(null, [bytes!, music, 150, cancel.Token])!; }
            catch (OperationCanceledException) { cancelled = true; }
            string file = CoverLoadQueue.GetThumbCachePath(music.ImageHash, 150);
            long length = File.Exists(file) ? new FileInfo(file).Length : 0;
            Assert(cancelled && !File.Exists(file), $"cancelled decode remains cancelled and cannot publish a partial thumbnail (cancelled={cancelled}, bytes={length})");
            await (Task)method.Invoke(null, [bytes!, music, 150, CancellationToken.None])!;
            byte[] original = await File.ReadAllBytesAsync(file);
            Assert(original.Length == 54 + 150 * 150 * 4, "native decode publishes a complete thumbnail");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                await (Task)method.Invoke(null, [bytes!, music, 150, CancellationToken.None])!;
            byte[] afterFailure = await File.ReadAllBytesAsync(file);
            Assert(original.SequenceEqual(afterFailure)
                && Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Length == 0,
                "a failed publish preserves the old readable cache and removes its temporary file");
            using var interrupted = new CancellationTokenSource();
            var inFlight = (Task)method.Invoke(null, [bytes!, music, 150, interrupted.Token])!;
            interrupted.Cancel();
            bool inFlightCancelled = false;
            try { await inFlight; }
            catch (OperationCanceledException) { inFlightCancelled = true; }
            byte[] afterCancel = await File.ReadAllBytesAsync(file);
            Assert(inFlightCancelled && original.SequenceEqual(afterCancel)
                && Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Length == 0,
                "cancellation after a decode starts preserves an existing thumbnail without leftover temporary files");
        }
        finally { AppSettings.MusicCoverCache = oldCache; }
    }
    private async Task PrepareHoverCheck()
    {
        _page!.CollapseDetail();
        await Wait(() => !_home.HasDetails);
        var scroll = (ScrollViewer)_page.FindName("ContentScroll");
        scroll.ChangeView(null, 520, null, true);
        await Task.Delay(100);
        var shelf = (RecommendationShelf)_page.FindName("SongsShelf");
        var source = Descendants<HomeSongItem>(shelf).First();
        typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_page, [source]);
        await Wait(() => _home.PreviewSong is not null);
        await Task.Delay(260);
    }
    private async Task CheckHoverExit()
    {
        await PrepareHoverCheck();
        var preview = (HomeSongPreview)_page!.FindName("SongPreview");
        typeof(HomePage).GetMethod("BeginPreviewClose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_page, null);
        await Wait(() => _home.PreviewSong is null);
        var visual = ElementCompositionPreview.GetElementVisual(preview);
        Assert(preview.Visibility == Visibility.Collapsed && visual.Opacity == 0,
            $"closed preview stays transparent instead of resetting to full-size visible content (opacity={visual.Opacity})");
    }
    private async Task CheckHoverWheel()
    {
        await PrepareHoverCheck();
        var preview = (HomeSongPreview)_page!.FindName("SongPreview");
        var scroll = (ScrollViewer)_page.FindName("ContentScroll");
        typeof(HomePage).GetMethod("Content_ViewChanging", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_page, [scroll, null]);
        Assert(_home.PreviewSong is not null && preview.Visibility == Visibility.Visible,
            "scroll callback retains the preview image while its real composition exit runs");
        await Wait(() => _home.PreviewSong is null);
    }
    private async Task CheckCoverCycles()
    {
        var cover = new MusicCoverControl { Width = 160, Height = 160, Music = _songs[0] };
        _retiredCovers.Add(new(cover));
        var host = new ContentControl { Content = cover, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(host, 1);
        var events = new List<object>();
        void Record(string action) => events.Add(new { Action = action, cover.IsLoaded,
            ChildLoaded = ((Grid)cover.FindName("CoverPresentation")).IsLoaded,
            Source = ((Image)cover.FindName("CoverImage")).Source?.GetType().Name,
            Clip = ElementCompositionPreview.GetElementVisual((Grid)cover.FindName("CoverPresentation")).Clip?.GetType().Name,
            Load = typeof(MusicCoverControl).GetField("_load", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cover) is not null });
        cover.Loaded += (_, _) => Record("loaded");
        cover.Unloaded += (_, _) => Record("unloaded");
        _root.Children.Add(host);
        try
        {
            for (int i = 0; i < 25; i++)
            {
                await Wait(() => cover.IsLoaded && ((Image)cover.FindName("CoverImage")).Source is not null);
                var visual = ElementCompositionPreview.GetElementVisual((FrameworkElement)cover.FindName("CoverPresentation"));
                Assert(visual.Clip is not null, "loaded cover retains rounding after actual reload " + i);
                host.Content = null;
                await Wait(() => !cover.IsLoaded);
                host.Content = cover;
            }
        }
        finally
        {
            Record("final");
            File.WriteAllText(Path.Combine(_output, "cover-cycle-events.json"), System.Text.Json.JsonSerializer.Serialize(events));
            host.Content = null;
            _root.Children.Remove(host);
        }
    }
}
