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
        _page.UpdateLayout();
        var shelf = (RecommendationShelf)_page.FindName("SongsShelf");
        var source = Descendants<HomeSongItem>(shelf).First();
        double sourceTop = source.TransformToVisual(scroll).TransformPoint(new Point()).Y + scroll.VerticalOffset;
        scroll.ChangeView(null, Math.Max(0, sourceTop - 80), null, true);
        await Task.Delay(100);
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


internal sealed partial class RegressionApplication
{
    private async Task CheckHoverHandoffAsync(HomePage page, WinUIMusicPlayer.ViewModel.HomeViewModel home)
    {
        ((FeaturedCarousel)page.FindName("Featured")).AutoShuffle = false;
        var scroll = (ScrollViewer)page.FindName("ContentScroll");
        var shelf = (RecommendationShelf)page.FindName("SongsShelf");
        page.UpdateLayout();
        var sources = Descendants<HomeSongItem>(shelf).Take(2).ToArray();
        double top = sources[0].TransformToVisual(scroll).TransformPoint(new Point()).Y + scroll.VerticalOffset;
        scroll.ChangeView(null, Math.Max(0, top - 100), null, true);
        await Task.Delay(100);
        var preview = (HomeSongPreview)page.FindName("SongPreview");
        var first = (WinUIMusicPlayer.ViewModel.HomeSong)sources[0].Tag;
        var second = (WinUIMusicPlayer.ViewModel.HomeSong)sources[1].Tag;
        void Call(string name, params object?[] arguments) => typeof(HomePage)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, arguments);
        void Schedule(HomeSongItem card) => Call("SchedulePreview", card);
        async Task WaitOpen(WinUIMusicPlayer.ViewModel.HomeSong song)
        {
            await Wait(() => ReferenceEquals(home.PreviewSong, song) && preview.Visibility == Visibility.Visible);
            await Task.Delay(260);
        }

        // 重复移动应继续第一次悬停的倒计时，而不是不断把 400ms 推迟。
        Schedule(sources[0]);
        await Task.Delay(300);
        Schedule(sources[0]);
        await Task.Delay(250);
        Check(ReferenceEquals(home.PreviewSong, first), "moving within a card does not restart its hover delay");
        await WaitOpen(first);
        Check(preview.ActualWidth == 280 && ((TextBlock)preview.FindName("RecommendationReason")).Text == first.Reason,
            "small shelf preview uses 280px and retains the fourth recommendation reason row");

        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            Schedule(sources[1]);
            await WaitOpen(second);
            Check(preview.IsHitTestVisible, "disabled animations still switch to an interactive target preview; animation-only checks skipped");
            home.DismissPreview();
            return;
        }

        // 原面板真实收起动画期间，重放旧面板进入与新卡片被短暂遮住的离开事件。
        Schedule(sources[1]);
        Check(!preview.IsHitTestVisible, "outgoing preview stops intercepting the target card's pointer input");
        Call("Preview_PointerEntered", preview, null);
        Call("LeavePreviewSource", sources[1], new Point(10, 10));
        Check(Field<bool>(page, "_collapsingPreview")
            && ReferenceEquals(Field<FrameworkElement>(page, "_pendingPreviewSource"), sources[1]),
            "animation-induced pointer transitions preserve the pending target instead of restoring the old preview");
        await WaitOpen(second);
        Check(preview.IsHitTestVisible && ReferenceEquals(preview.Song, second),
            "native close completion opens the target and restores its action hit testing");

        // 真正离开目标必须取消请求，不能在已离开后弹出过期面板。
        Schedule(sources[0]);
        Call("LeavePreviewSource", sources[0], new Point(-1, -1));
        await Wait(() => home.PreviewSong is null);
        await Task.Delay(450);
        Check(preview.Visibility == Visibility.Collapsed,
            "leaving the pending card cancels its delayed preview");

        // 收起时返回原卡片应取消切换；旧 batch 的迟到完成不得清掉恢复后的面板。
        Schedule(sources[0]);
        await WaitOpen(first);
        Schedule(sources[1]);
        Schedule(sources[0]);
        await Task.Delay(250);
        Check(ReferenceEquals(home.PreviewSong, first) && preview.IsHitTestVisible
            && !Field<bool>(page, "_collapsingPreview"),
            "returning to the original card restores it and rejects stale close completion");
        home.DismissPreview();
    }
}
