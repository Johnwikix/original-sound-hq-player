using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using SQLite;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Recommendations;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new RegressionApplication();
        });
    }
}
internal sealed partial class RegressionApplication : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider _native = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    private readonly IXamlMetadataProvider _local = (IXamlMetadataProvider)Activator.CreateInstance(typeof(Program).Assembly.GetTypes()
        .Single(t => t.FullName!.EndsWith(".XamlMetaDataProvider", StringComparison.Ordinal)))!;
    public IXamlType GetXamlType(Type type) => _local.GetXamlType(type) ?? _native.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _local.GetXamlType(name) ?? _native.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private readonly List<string> _results = [];
    private readonly string _resultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");
    public RegressionApplication() => UnhandledException += (_, args) => File.WriteAllText(_resultPath, "FAIL: unhandled " + args.Exception);
    private void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _results.Add("PASS: " + name);
    }
    private static async Task Wait(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(25); }
        throw new TimeoutException("UI condition did not become true");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static int ChangedPixels(byte[] before, byte[] after)
    {
        int changed = 0;
        for (int i = 0; i < Math.Min(before.Length, after.Length); i += 4)
            if (before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2]) changed++;
        return changed;
    }
    private static async Task Capture(FrameworkElement element, string name)
    {
        var image = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await image.RenderAsync(element);
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)image.PixelWidth,
            (uint)image.PixelHeight, 96, 96, (await image.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixture.db");
        if (File.Exists(path)) File.Delete(path);
        var database = new SQLiteAsyncConnection(path);
        Window? window = null;
        HomeViewModel? home = null;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            await database.ExecuteAsync("CREATE TABLE PlaybackHistory (MusicId INTEGER, StartedAt INTEGER, TotalDurationMs REAL, DurationPlayedMs REAL)");
            await database.ExecuteAsync("INSERT INTO PlaybackHistory VALUES (1,?,100000,90000)", DateTime.UtcNow.AddDays(-2).Ticks);
            for (int i = 2; i <= 20; i++)
                await database.ExecuteAsync("INSERT INTO PlaybackHistory VALUES (?, ?,100000,80000)", i, DateTime.UtcNow.AddDays(-2).Ticks);
            var app = new AppViewModel();
            var folders = new AddFolderViewModel();
            var statistics = new PlaybackStatsService();
            var playback = new PlaybackCoordinator(app, statistics);
            var remote = new WebDavLibraryService();
            var data = new MusicDatabaseService(database);
            home = new(app, data, statistics, new HomeRecommendationService(), playback, folders, remote, NullLogger<HomeViewModel>.Instance);
            using var services = new ServiceCollection().AddSingleton(home).BuildServiceProvider();
            App.Services = services;
            window = new Window();
            App.MainWindow = window;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 820));
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            var page = new HomePage { RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243)) };
            window.Content = page;
            window.Activate();
            await Task.Delay(220);
            Check(home.IsLoading && !home.IsEmpty, "real page distinguishes initial loading from an empty library");
            app.Replace([]);
            await Wait(() => home.IsEmpty);
            Check(((Grid)page.FindName("EmptyLibraryGrid")).Visibility == Visibility.Visible, "shipping empty-state compiled binding becomes visible");
            Check(Descendants<TextBlock>(page).Any(t => t.Text == "Make room for your music"), "native x:Uid resources resolve the empty-state title");
            await Capture(page, "home-empty.png");
            folders.IsScanning = true;
            await Task.Delay(30);
            Check(home.IsScanning && Descendants<ProgressRing>(page).Any(p => p.IsActive), "empty library shows the shared scanning state");
            folders.IsScanning = false;
            var songs = Enumerable.Range(1, 40).Select(i => new Music { Id = i, Title = "Song " + i, Author = "Artist " + i,
                Album = "Album " + i, Genre = i % 3 == 0 ? "Jazz" : "Pop", Year = 2000 + i % 20,
                ImageHash = "fixture-" + i, Path = Path.Combine(AppContext.BaseDirectory, "track-" + i + ".flac") }).ToList();
            foreach (var song in songs) WinUIMusicPlayer.Utils.ToolUtils.PrepareArtwork(song.Id);
            bool offThread = false;
            home.PropertyChanged += (_, _) => offThread |= !DispatcherQueue.GetForCurrentThread().HasThreadAccess;
            app.Replace(songs);
            await Wait(() => home.HasContent && !home.IsLoading);
            Check(((ScrollViewer)page.FindName("ContentScroll")).Visibility == Visibility.Visible && !home.IsEmpty,
                "first imported batch replaces empty state before overall scanning completes");
            Check(home.Familiar.Count == 20 && ReferenceEquals(home.Songs[0].Music, app.FindById(home.Songs[0].Music.Id)),
                "real SQLite history and canonical music objects reach the shipping page");
            Check(!home.PlaySongCommand.CanExecute(home.Songs[0]), "play remains guarded until engine and lifecycle are ready");
            Check(Descendants<ListView>(page).SelectMany(list => Descendants<Button>(list))
                .Where(b => ReferenceEquals(b.Command, home.PlaySongCommand)).All(b => !b.IsEnabled),
                "whole-cover play buttons stay disabled until playback is ready");
            app.State.Lifecycle.TransitionTo(AppPhase.Initializing);
            app.State.Lifecycle.TransitionTo(AppPhase.Ready);
            app.IsPlaybackEngineReady = true;
            await Wait(() => Descendants<MusicCoverControl>(page).Any(c => c.DecodePixelSize == 800 && c.ArtworkColor.A == 255));
            var initialSongs = home.Songs;
            var initialThemes = home.Themes;
            var carousel = (FeaturedCarousel)page.FindName("Featured");
            carousel.AutoShuffle = false; // 手动导航断言需要确定时序，自动轮播单独验证。
            CarouselLighting[] lights = [Field<CarouselLighting>(carousel, "_lighting")];
            Check(lights.All(l => l.IsAttached), "hero and collage share lighting within the two non-ambient light limit");
            var lightShade = (GradientStop)((HomeHeroCard)page.FindName("HomeHero")).FindName("ShadeStart");
            var lightTitle = (TextBlock)((HomeHeroCard)page.FindName("HomeHero")).FindName("TitleText");
            Check(lightShade.Color.A == 128 && lightShade.Color.R > 200 && lightShade.Color.R == lightShade.Color.G && lightShade.Color.G == lightShade.Color.B
                && lightTitle.Foreground is SolidColorBrush lightFg && lightFg.Color.R < 100,
                "light theme pairs a light scrim with dark hero text");
            var slider = (Grid)carousel.FindName("SliderGrid");
            var primary = (Button)carousel.FindName("PrimaryThumbnail");
            var secondary = (Button)carousel.FindName("SecondaryThumbnail");
            Check(slider.ActualWidth / slider.ActualHeight is > 1.16 and < 1.3
                && primary.ActualWidth / primary.ActualHeight is > 1.2 and < 1.65
                && secondary.ActualWidth / secondary.ActualHeight is > .8 and < 1.15,
                $"main and collage artwork use near-square rectangular proportions (slider {slider.ActualWidth:F0}x{slider.ActualHeight:F0}, primary {primary.ActualWidth:F0}x{primary.ActualHeight:F0}, secondary {secondary.ActualWidth:F0}x{secondary.ActualHeight:F0})");
            var shelf = Descendants<ListView>(page).First();
            var shelfScroll = Descendants<ScrollViewer>(shelf).First();
            shelfScroll.ChangeView(140, null, null, true);
            await Task.Delay(100);
            double shelfOffset = shelfScroll.HorizontalOffset;
            var coverBeforePlay = Descendants<MusicCoverControl>(shelf).First();
            var sourceBeforePlay = ((Image)coverBeforePlay.FindName("CoverImage")).Source;
            var button = Descendants<Button>(shelf).First(b => ReferenceEquals(b.Command, home.PlaySongCommand));
            Check(button.IsEnabled && button.CommandParameter is HomeSong, "shipping template binds live commands and parameters");
            var selected = (HomeSong)button.CommandParameter;
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => playback.LastId == selected.Music.Id);
            Check(app.SequentialPlayingList.Select(m => m.Id).SequenceEqual(selected.Queue.Select(m => m.Id)),
                "native button invocation plays the explicit shelf queue");
            foreach (string name in new[] { "PrimaryThumbnail", "SecondaryThumbnail", "TertiaryThumbnail" })
            {
                var tile = (Button)carousel.FindName(name);
                var song = (HomeSong)tile.CommandParameter;
                playback.LastId = 0;
                var tilePeer = new ButtonAutomationPeer(tile);
                ((IInvokeProvider)tilePeer.GetPattern(PatternInterface.Invoke)).Invoke();
                await Wait(() => playback.LastId == song.Music.Id);
                Check(app.SequentialPlayingList.Select(m => m.Id).SequenceEqual(song.Queue.Select(m => m.Id)),
                    name + " starts its song in the recommendation queue without opening music properties");
            }
            await Task.Delay(450);
            Check(ReferenceEquals(initialSongs, home.Songs) && ReferenceEquals(initialThemes, home.Themes)
                && Math.Abs(shelfOffset - shelfScroll.HorizontalOffset) < .5
                && ReferenceEquals(sourceBeforePlay, ((Image)coverBeforePlay.FindName("CoverImage")).Source),
                "playback selection and history settlement preserve shelf objects, cover sources and horizontal scroll position");
            foreach (var list in Descendants<ListView>(page))
            {
                var covers = Descendants<Button>(list).Where(b => ReferenceEquals(b.Command, home.PlaySongCommand)).ToArray();
                if (covers.Length == 0) continue;
                Check(covers.Length == Descendants<MusicCoverControl>(list).Count()
                    && covers.All(b => b.ActualWidth == 160 && b.ActualHeight == 160
                        && b.Content is MusicCoverControl
                        && b.Parent is StackPanel card && card.Children.OfType<StackPanel>().Single().Children.Count == 2),
                    "shelf exposes one full-cover play action per song and only favorite/queue footer actions");
                var cover = covers[0];
                var song = (HomeSong)cover.CommandParameter;
                playback.LastId = 0;
                ((IInvokeProvider)new ButtonAutomationPeer(cover).GetPattern(PatternInterface.Invoke)).Invoke();
                await Wait(() => playback.LastId == song.Music.Id);
                Check(app.SequentialPlayingList.Select(m => m.Id).SequenceEqual(song.Queue.Select(m => m.Id)),
                    "native shelf cover click plays its own recommendation, familiar or discovery queue");
            }
            var playOverlay = Descendants<Border>(button).Single(b => b.Name == "PlayOverlay");
            var keyboardOverlay = Descendants<Border>(button).Single(b => b.Name == "KeyboardPlayOverlay");
            Check(playOverlay.Opacity == 0 && keyboardOverlay.Opacity == 0,
                "normal cover keeps its artwork unobstructed");
            VisualStateManager.GoToState(button, "PointerOver", false);
            Check(playOverlay.Opacity == 1 && !playOverlay.IsHitTestVisible
                && Descendants<FontIcon>(playOverlay).Single().Glyph == "\uE768",
                "hover dims the cover and reveals a centered play icon without blocking the button");
            await Capture(button, "home-cover-hover.png");
            VisualStateManager.GoToState(button, "Normal", false);
            button.Focus(FocusState.Keyboard);
            await Task.Delay(60);
            Check(keyboardOverlay.Opacity == 1, "keyboard focus exposes the same cover play affordance");
            var favorite = Descendants<Button>(shelf).First(b => ReferenceEquals(b.Command, WinUIMusicPlayer.Services.MusicCommands.UpdateFavouriteCommand));
            Check(Descendants<FontIcon>(favorite).Single().Foreground is SolidColorBrush heart && heart.Color == Microsoft.UI.Colors.Red,
                "favorite heart follows the existing red icon convention");
            var queueButton = Descendants<Button>(shelf).First(b => ReferenceEquals(b.Command, WinUIMusicPlayer.Services.MusicCommands.AddToPlayListCommand));
            Check(ToolTipService.GetToolTip(queueButton)?.ToString() == "Add to current playback queue", "queue plus button explains its purpose on hover");
            var refreshButton = (Button)page.FindName("RefreshButton");
            Check(refreshButton.BorderThickness == new Thickness(0) && refreshButton.Foreground is SolidColorBrush,
                "refresh action has no border and uses a theme foreground");
            foreach (var list in Descendants<ListView>(page))
            {
                var actions = Descendants<Button>(list).Where(b => ReferenceEquals(b.Command, WinUIMusicPlayer.Services.MusicCommands.UpdateFavouriteCommand)
                    || ReferenceEquals(b.Command, WinUIMusicPlayer.Services.MusicCommands.AddToPlayListCommand)).ToArray();
                if (actions.Length == 0) continue;
                double bottom = actions.Max(b => b.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point(0, b.ActualHeight)).Y);
                var bar = Descendants<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(list)
                    .FirstOrDefault(b => b.Orientation == Orientation.Horizontal && b.ActualHeight > 0);
                if (bar is null) continue;
                Check(bottom + 8 <= bar.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point()).Y,
                    "horizontal scrollbar stays below shelf actions with reserved spacing");
            }
            var artwork = await WinUIMusicPlayer.Utils.CoverLoadQueue.EnqueueArtworkAsync(songs[0], default, 800);
            var regular = await WinUIMusicPlayer.Utils.CoverLoadQueue.EnqueueAsync(songs[0], default);
            Check(artwork.Source is Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource && regular is Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource
                && !ReferenceEquals(artwork.Source, regular) && artwork.EdgeColor.A == 255,
                "shipping cover queue performs real WIC decode and gives hero and shelf independently owned sources");
            (artwork.Source as IDisposable)?.Dispose();
            (regular as IDisposable)?.Dispose();
            var sharedArtwork = await Task.WhenAll(
                WinUIMusicPlayer.Utils.CoverLoadQueue.EnqueueArtworkAsync(songs[0], default, 800),
                WinUIMusicPlayer.Utils.CoverLoadQueue.EnqueueArtworkAsync(songs[0], default, 800));
            Check(sharedArtwork[0].Source is not null && sharedArtwork[1].Source is not null
                && !ReferenceEquals(sharedArtwork[0].Source, sharedArtwork[1].Source),
                "concurrent same-size consumers own separate native image sources");
            foreach (var item in sharedArtwork) (item.Source as IDisposable)?.Dispose();
            Check(File.Exists(WinUIMusicPlayer.Utils.CoverLoadQueue.GetThumbCachePath(songs[0].ImageHash, 800))
                && File.Exists(WinUIMusicPlayer.Utils.CoverLoadQueue.GetThumbCachePath(songs[0].ImageHash, 150)),
                "hero and shelf sizes use separate entries in the existing pixel cache");
            WinUIMusicPlayer.Utils.ToolUtils.PrepareCenteredArtwork(98);
            var centerCover = new MusicCoverControl
            {
                Width = 60, Height = 240, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Music = new Music { Id = 98, ImageHash = "fixture-centered", Path = "missing-centered.flac" }
            };
            Grid.SetRow(centerCover, 1);
            var pageGrid = (Grid)page.Content;
            pageGrid.Children.Add(centerCover);
            try
            {
                await Wait(() => ((Image)centerCover.FindName("CoverImage")).Source is not null);
                await Task.Delay(60);
                await Capture(centerCover, "home-center-crop.png");
                var rendered = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await rendered.RenderAsync(centerCover);
                var pixels = (await rendered.GetPixelsAsync()).ToArray();
                int left = (rendered.PixelHeight / 2 * rendered.PixelWidth + 8) * 4;
                int right = (rendered.PixelHeight / 2 * rendered.PixelWidth + rendered.PixelWidth - 8) * 4;
                Check(pixels[left + 1] > 200 && pixels[left + 2] < 40 && pixels[right + 1] > 200 && pixels[right] < 40,
                    "cover cropping preserves the center strip rather than anchoring at either edge");
            }
            finally { pageGrid.Children.Remove(centerCover); }
            // CoverLoadQueue 工作线程写 ImageHash 曾在 PropertyChanged 里同步触碰 UI 抛 0x8001010E，导致首页封面整链路失败。
            WinUIMusicPlayer.Utils.ToolUtils.PrepareArtwork(97);
            var marshalCover = new MusicCoverControl
            {
                Width = 60, Height = 60, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Music = new Music { Id = 97, ImageHash = "fixture-marshal", Path = "missing-marshal.flac" }
            };
            Grid.SetRow(marshalCover, 1);
            pageGrid.Children.Add(marshalCover);
            try
            {
                await Wait(() => marshalCover.IsLoaded);
                var marshalMusic = marshalCover.Music!;
                var loadBefore = Field<CancellationTokenSource>(marshalCover, "_load");
                await Task.Run(() => marshalMusic.ImageHash = "fixture-marshal-2");
                Check(marshalMusic.ImageHash == "fixture-marshal-2",
                    "background ImageHash writes no longer marshal into the UI thread synchronously");
                await Wait(() => !ReferenceEquals(Field<CancellationTokenSource>(marshalCover, "_load"), loadBefore));
            }
            finally { pageGrid.Children.Remove(marshalCover); }
            // 无封面时占位音符随卡片尺寸等比放大；固定 44px 在拼贴大卡上几乎不可见，像加载失败。
            var bareCover = new MusicCoverControl
            {
                Width = 380, Height = 260, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Music = new Music { Id = 96, ImageHash = "fixture-bare", Path = "missing-bare.flac" }
            };
            Grid.SetRow(bareCover, 1);
            pageGrid.Children.Add(bareCover);
            try
            {
                var barePlaceholder = (FontIcon)bareCover.FindName("CoverPlaceholder");
                await Wait(() => bareCover.ActualWidth > 300 && barePlaceholder.FontSize > 50);
                Check(barePlaceholder.Visibility == Visibility.Visible && ((Image)bareCover.FindName("CoverImage")).Source is null,
                    "large collage tiles show a proportional no-artwork placeholder instead of a blank slab");
            }
            finally { pageGrid.Children.Remove(bareCover); }
            WinUIMusicPlayer.Utils.ToolUtils.PrepareArtwork(99, 64, 4096);
            var portrait = new Music { Id = 99, ImageHash = "fixture-portrait", Path = "missing-portrait.flac" };
            var portraitArtwork = await WinUIMusicPlayer.Utils.CoverLoadQueue.EnqueueArtworkAsync(portrait, default, 800);
            (portraitArtwork.Source as IDisposable)?.Dispose();
            var portraitBytes = File.ReadAllBytes(WinUIMusicPlayer.Utils.CoverLoadQueue.GetThumbCachePath(portrait.ImageHash, 800));
            int portraitHeight = Math.Abs(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(portraitBytes.AsSpan(22)));
            Check(portraitHeight <= 2048, "large carousel decoding bounds extreme portrait artwork allocations");
            await Capture(page, "home-populated.png");
            page.RequestedTheme = ElementTheme.Dark;
            page.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32));
            await Task.Delay(80);
            var darkShade = (GradientStop)((HomeHeroCard)page.FindName("HomeHero")).FindName("ShadeStart");
            Check(darkShade.Color.A == 128 && darkShade.Color.R == darkShade.Color.G && darkShade.Color.G == darkShade.Color.B,
                "hero shade stays a fixed-strength theme neutral instead of following artwork colors");
            await Capture(page, "home-dark.png");
            var unlitFrame = await WindowCapture.CaptureAsync(window, "home-window-rest.png");
            var hover = Field<Microsoft.UI.Composition.SpotLight>(lights[0], "_light");
            var ripple = Field<Microsoft.UI.Composition.SpotLight>(lights[0], "_light");
            var ambient = Field<Microsoft.UI.Composition.AmbientLight>(lights[0], "_ambient");
            lights[0].ShowHover(new Vector2((float)slider.ActualWidth * .7f, (float)slider.ActualHeight * .4f), false);
            await Task.Delay(220);
            Check(ambient.Targets.Count == 1 && hover.Targets.Count == 1 && ripple.Targets.Count == 1
                && hover.CoordinateSpace == ElementCompositionPreview.GetElementVisual((Grid)carousel.FindName("LayoutGrid"))
                && Math.Abs(hover.Offset.X - slider.ActualWidth * .7) < .1,
                "lighting targets and pointer offsets remain inside their carousel region");
            await Capture(page, "home-hover.png");
            var litFrame = await WindowCapture.CaptureAsync(window, "home-window-hover.png");
            Check(ChangedPixels(unlitFrame, litFrame) > 100, "actual window rendering shows the composition hover light rather than only allocating light objects");
            lights[0].Pulse(new Vector2((float)slider.ActualWidth * .7f, (float)slider.ActualHeight * .4f));
            var depthController = ripple.TryGetAnimationController("Offset");
            var intensityController = ripple.TryGetAnimationController("InnerConeIntensity");
            Check(depthController is not null && intensityController is not null,
                "press ripple animates native spotlight depth and intensity");
            // 本机离屏快照不体现独立合成动画的完整时序；不把静态帧当作轨迹验收。
            lights[0].HideHover();
            await Task.Delay(650);
            var header = (Grid)page.FindName("HomeHeader");
            double headerTop = header.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).Y;
            ((ScrollViewer)page.FindName("ContentScroll")).ChangeView(null, 470, null, true);
            await Task.Delay(100);
            Check(Math.Abs(headerTop - header.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).Y) < .1
                && headerTop + header.ActualHeight <= ((ScrollViewer)page.FindName("ContentScroll"))
                    .TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).Y,
                "home heading and refresh action stay above the scrolling viewport");
            await Capture(page, "home-shelves.png");
            ((ScrollViewer)page.FindName("ContentScroll")).ChangeView(null, 0, null, true);
            var queueBefore = app.SequentialPlayingList;
            int queriesBeforeRefresh = data.QueriesStarted;
            var queryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.WhenInitialized = queryGate.Task;
            home.RefreshCommand.Execute(null);
            await Wait(() => data.QueriesStarted > queriesBeforeRefresh);
            statistics.Notify();
            await Task.Delay(40);
            queryGate.SetResult();
            data.WhenInitialized = Task.CompletedTask;
            await Wait(() => !ReferenceEquals(initialSongs, home.Songs));
            await Task.Delay(150);
            Check(data.QueriesStarted == queriesBeforeRefresh + 1,
                "listening updates during an in-flight refresh stay deferred without another immediate query");
            Check(ReferenceEquals(queueBefore, app.SequentialPlayingList), "refresh does not rewrite an already selected playback queue");
            var collage = (Grid)carousel.FindName("ThumbnailGrid");
            Check(Grid.GetColumn(collage) == 1 && Grid.GetColumnSpan((Button)carousel.FindName("PrimaryThumbnail")) == 2
                && Grid.GetRow((Button)carousel.FindName("SecondaryThumbnail")) == 1,
                "wide Store layout has one large thumbnail above two smaller thumbnails");
            Check(((PipsPager)carousel.FindName("Pager")).NumberOfPages == home.Themes.Count,
                "Store carousel uses interactive dot pagination");
            Check(home.Themes.Count == 5 && home.Themes.Select(t => t.Artwork).Distinct().Count() == 5,
                "three shelves fill five distinct featured pages");
            Check(carousel.ItemsSource!.Count >= 2 && carousel.SelectedItem is HomeTheme, "featured control receives theme snapshots through compiled binding");
            var next = (Button)carousel.FindName("NextButton");
            ((Button)((HomeHeroCard)page.FindName("HomeHero")).FindName("PlayButton")).Focus(FocusState.Keyboard);
            next.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            Check(next.Visibility == Visibility.Visible && next.OpacityTransition is null && next.ScaleTransition is null,
                "arrow actions use upstream direct visibility with keyboard access");
            refreshButton.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            Check(next.Visibility == Visibility.Collapsed, "hidden arrow actions leave no invisible pointer hit area");
            carousel.Move(1);
            Check(carousel.SelectedIndex == 1, "featured control navigates synchronously with autoplay disabled");
            ((PipsPager)carousel.FindName("Pager")).SelectedPageIndex = 0;
            Check(carousel.SelectedIndex == 0, "dot pagination updates the selected carousel content");
            carousel.ShuffleDuration = TimeSpan.FromMilliseconds(150);
            carousel.AutoShuffle = true;
            await Wait(() => carousel.SelectedIndex != 0);
            int autoAdvanced = carousel.SelectedIndex;
            Check(autoAdvanced > 0, "store autoplay advances the featured pages automatically");
            typeof(FeaturedCarousel).GetMethod("Carousel_PointerEntered", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(carousel, [null, null]);
            await Task.Delay(500);
            Check(carousel.SelectedIndex == autoAdvanced, "hover pauses autoplay like the upstream store carousel");
            typeof(FeaturedCarousel).GetMethod("Carousel_PointerExited", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(carousel, [null, null]);
            await Wait(() => carousel.SelectedIndex != autoAdvanced);
            carousel.AutoShuffle = false;
            var baseTheme = home.Themes[0];
            var differentArtwork = songs.First(m => !ReferenceEquals(m, baseTheme.Artwork));
            var source = new ObservableCollection<HomeTheme> { baseTheme, baseTheme with { Artwork = differentArtwork, Title = "Other artwork" } };
            // 自动轮播可能停在任意页；先归零再换源，避免 PipsPager 因 NumberOfPages 收缩 clamp 出非零选中页。
            ((PipsPager)carousel.FindName("Pager")).SelectedPageIndex = 0;
            carousel.ItemsSource = source;
            var stableCard = (HomeHeroCard)page.FindName("HomeHero");
            var animatedCover = (MusicCoverControl)stableCard.FindName("Artwork");
            await Wait(() => ReferenceEquals(animatedCover.Music, baseTheme.Artwork) && ((Image)animatedCover.FindName("CoverImage")).Source is not null);
            var firstImage = ((Image)animatedCover.FindName("CoverImage")).Source;
            carousel.Move(1);
            await Wait(() => ReferenceEquals(animatedCover.Music, differentArtwork)
                && ((Image)animatedCover.FindName("CoverImage")).Source is { } image && !ReferenceEquals(image, firstImage));
            var secondImage = ((Image)animatedCover.FindName("CoverImage")).Source;
            Check(ReferenceEquals(stableCard, Descendants<HomeHeroCard>(carousel).Single())
                && ReferenceEquals(firstImage, ((Image)animatedCover.FindName("PreviousImage")).Source)
                && carousel.SlideFromRight && animatedCover.SlideFromRight,
                "forward slide preserves the stable hero and previous owned cover behind the new artwork");
            var currentLayer = (Grid)animatedCover.FindName("CurrentLayer");
            var previousLayer = (Grid)animatedCover.FindName("PreviousLayer");
            Check(ElementCompositionPreview.GetElementVisual(currentLayer).TryGetAnimationController("Offset") is not null
                && ElementCompositionPreview.GetElementVisual(previousLayer).TryGetAnimationController("Opacity") is not null,
                "upstream image slide and previous-image fade run on real composition visuals");
            carousel.Move(-1);
            await Wait(() => ReferenceEquals(animatedCover.Music, baseTheme.Artwork)
                && ((Image)animatedCover.FindName("CoverImage")).Source is { } image && !ReferenceEquals(image, secondImage));
            Check(!carousel.SlideFromRight && !animatedCover.SlideFromRight
                && ReferenceEquals(secondImage, ((Image)animatedCover.FindName("PreviousImage")).Source),
                "reverse slide enters from the left while fading the actual previous cover");
            source[1] = baseTheme with { Title = "Same cover, other title" };
            var sameArtworkSource = ((Image)animatedCover.FindName("CoverImage")).Source;
            carousel.Move(1);
            await Task.Delay(100);
            Check(Descendants<TextBlock>(carousel).Any(t => t.Text == "Same cover, other title")
                && ReferenceEquals(sameArtworkSource, ((Image)animatedCover.FindName("CoverImage")).Source),
                "same artwork updates text and commands without replacing or decoding the displayed image");
            var movingCard = Descendants<HomeHeroCard>(carousel).First();
            var motionTitle = (TextBlock)movingCard.FindName("TitleText");
            var motionDescription = (TextBlock)movingCard.FindName("DescriptionText");
            var motionButton = (Button)movingCard.FindName("PlayButton");
            var actionPanel = (StackPanel)movingCard.FindName("ActionPanel");
            movingCard.Theme = movingCard.Theme! with { Title = "Motion title", Description = "Motion description" };
            await Task.Delay(60);
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                Check(actionPanel.Opacity is > 0 and < 1 && motionTitle.Opacity == 1 && motionButton.Opacity == 1
                    && actionPanel.RenderTransform is CompositeTransform { TranslateX: > 0, ScaleX: < 1 } transform
                    && Math.Abs(transform.ScaleX - (.8 + .2 * actionPanel.Opacity)) < .04
                    && Math.Abs(transform.TranslateX - 100 * (1 - actionPanel.Opacity)) < 5,
                    "upstream action panel moves 100px and scales from 0.8 in sync with its whole-group linear fade");
            movingCard.Theme = movingCard.Theme! with { Title = "Latest title" };
            await Task.Delay(820);
            await Capture(page, "home-animation-rest.png");
            await Task.Delay(60);
            Check(motionTitle.Text == "Latest title" && motionTitle.Opacity > .99 && motionDescription.Opacity > .79
                && motionButton.Opacity > .99 && actionPanel.Opacity > .99
                && actionPanel.RenderTransform is CompositeTransform { TranslateX: < .1, ScaleX: > .99 },
                "rapid theme changes finish the shared 700ms action-panel animation without a stale transform");
            source.RemoveAt(1);
            Check(carousel.SelectedIndex == 0 && !carousel.CanNavigate, "shrinking observable collection clamps selection");
            source.Clear();
            Check(carousel.SelectedItem is null && carousel.Position == "0 / 0", "empty carousel never indexes the old selection");
            await Task.Delay(40);
            Check(((Image)animatedCover.FindName("CoverImage")).Source is null
                && ((Image)animatedCover.FindName("PreviousImage")).Source is null,
                "empty selection releases both retained image sources");
            lights[0].Pulse(new Vector2(80, 80));
            window.Content = new Grid();
            await Task.Delay(140);
            Check(WinUIMusicPlayer.Utils.ToolUtils.RawActive == 0 && !home.PlaySongCommand.CanExecute(selected),
                "unloading cancels cover consumption and disables page playback");
            Check(lights.All(l => !l.IsAttached), "unloading releases native lights and an in-flight press animation");
            window.Content = page;
            await Task.Delay(400);
            Check(home.HasContent, "same-day page reentry restores recommendations after deferred listening updates");
            Check(lights.All(l => l.IsAttached), "page reentry recreates lighting without duplicate subscriptions");
            home.RefreshCommand.Execute(null);
            await Task.Delay(400);
            Check(((FeaturedCarousel)page.FindName("Featured")).ItemsSource!.Count > 0 && home.PlaySongCommand.CanExecute(home.Songs[0]),
                "reloaded page restarts compiled tracking and commands");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 740));
            await Task.Delay(820);
            var card = Descendants<HomeHeroCard>(page).First();
            Check(((MusicCoverControl)card.FindName("Artwork")).Visibility == Visibility.Visible
                && Grid.GetRow(collage) == 0 && Grid.GetColumn(collage) == 1
                && Grid.GetRowSpan((Button)carousel.FindName("PrimaryThumbnail")) == 1
                && primary.ActualWidth < 250 && slider.ActualWidth / slider.ActualHeight is > 1.16 and < 1.3,
                "narrow carousel and collage stay side by side and shrink together without enlarging wrapped tiles");
            await Capture(page, "home-narrow.png");
            Check(!offThread, "view-model state notifications stay on the real WinUI dispatcher");
            var retained = home.Songs;
            data.WhenInitialized = Task.FromException(new IOException("fixture database failure"));
            statistics.Notify();
            home.RefreshCommand.Execute(null);
            await Wait(() => home.ErrorMessage.Length > 0);
            Check(ReferenceEquals(retained, home.Songs) && home.HasContent, "failed history refresh retains the usable previous snapshot");
            app.Replace([]);
            await Wait(() => home.IsEmpty);
            Check(home.ErrorMessage.Length == 0 && !home.HasContent, "clearing the library releases shelves and clears an irrelevant recommendation error");
            data.WhenInitialized = Task.CompletedTask;
            app.Replace(songs);
            home.RefreshCommand.Execute(null);
            await Wait(() => home.ErrorMessage.Length == 0 && home.HasContent && !home.IsLoading);
            Check(home.HasContent, "retry clears the failure after a successful real query");
            foreach (var music in songs) music.IsRemoteOffline = true;
            remote.Notify(1, true);
            await Wait(() => home.IsUnavailable);
            Check(!home.IsEmpty && !home.HasContent, "unavailable music sources remain distinct from an empty library");
            using var blockingConnection = new SQLiteConnection(path);
            blockingConnection.Execute("BEGIN EXCLUSIVE");
            try
            {
                int previousQueries = data.QueriesStarted;
                statistics.Notify();
                home.RefreshCommand.Execute(null);
                await Wait(() => data.QueriesStarted > previousQueries);
                await Task.Delay(60);
                app.State.Lifecycle.TryBeginExit(out _);
                var stopping = home.StopAsync();
                Check(!stopping.IsCompleted, "stop waits for the actual SQLite query rather than only cancelling its await");
                blockingConnection.Execute("ROLLBACK");
                await stopping;
            }
            finally { if (blockingConnection.IsInTransaction) blockingConnection.Execute("ROLLBACK"); }
            int countAfterStop = home.Songs.Count;
            await Task.Run(statistics.Notify);
            await Task.Delay(180);
            Check(home.Songs.Count == countAfterStop && !home.PlaySongCommand.CanExecute(selected), "stop drains work and rejects late cross-thread notifications");
            File.WriteAllText(_resultPath, string.Join(Environment.NewLine, _results));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(_resultPath, string.Join(Environment.NewLine, _results) + "\nFAIL: " + ex);
        }
        finally
        {
            if (home is not null) await home.StopAsync();
            window?.Close();
            WinUIMusicPlayer.Utils.CoverLoadQueue.Shutdown();
            await database.CloseAsync();
            Exit();
        }
    }
}
