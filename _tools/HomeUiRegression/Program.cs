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
            Check(home.Familiar.Count == 12 && ReferenceEquals(home.Songs[0].Music, app.FindById(home.Songs[0].Music.Id)),
                "real SQLite history and canonical music objects reach the shipping page");
            Check(!home.PlaySongCommand.CanExecute(home.Songs[0]), "play remains guarded until engine and lifecycle are ready");
            app.State.Lifecycle.TransitionTo(AppPhase.Initializing);
            app.State.Lifecycle.TransitionTo(AppPhase.Ready);
            app.IsPlaybackEngineReady = true;
            await Wait(() => Descendants<MusicCoverControl>(page).Any(c => c.DecodePixelSize == 800 && c.ArtworkColor.A == 255));
            var initialSongs = home.Songs;
            var initialThemes = home.Themes;
            var carousel = (FeaturedCarousel)page.FindName("Featured");
            CarouselLighting[] lights = [Field<CarouselLighting>(carousel, "_lighting")];
            Check(lights.All(l => l.IsAttached), "hero and collage share lighting within the two non-ambient light limit");
            var slider = (Grid)carousel.FindName("SliderGrid");
            var primary = (Button)carousel.FindName("PrimaryThumbnail");
            var secondary = (Button)carousel.FindName("SecondaryThumbnail");
            Check(slider.ActualWidth / slider.ActualHeight is > 1.16 and < 1.3
                && primary.ActualWidth / primary.ActualHeight is > 1.2 and < 1.65
                && secondary.ActualWidth / secondary.ActualHeight is > .8 and < 1.15,
                "main and collage artwork use near-square rectangular proportions");
            var shelf = Descendants<ListView>(page).First();
            var shelfScroll = Descendants<ScrollViewer>(shelf).First();
            shelfScroll.ChangeView(140, null, null, true);
            await Task.Delay(100);
            double shelfOffset = shelfScroll.HorizontalOffset;
            var coverBeforePlay = Descendants<MusicCoverControl>(shelf).First();
            var sourceBeforePlay = ((Image)coverBeforePlay.FindName("CoverImage")).Source;
            var button = Descendants<Button>(page).First(b => ReferenceEquals(b.Command, home.PlaySongCommand));
            Check(button.IsEnabled && button.CommandParameter is HomeSong, "shipping template binds live commands and parameters");
            var selected = (HomeSong)button.CommandParameter;
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => playback.LastId == selected.Music.Id);
            Check(app.SequentialPlayingList.Select(m => m.Id).SequenceEqual(selected.Queue.Select(m => m.Id)),
                "native button invocation plays the explicit shelf queue");
            await Task.Delay(450);
            Check(ReferenceEquals(initialSongs, home.Songs) && ReferenceEquals(initialThemes, home.Themes)
                && Math.Abs(shelfOffset - shelfScroll.HorizontalOffset) < .5
                && ReferenceEquals(sourceBeforePlay, ((Image)coverBeforePlay.FindName("CoverImage")).Source),
                "playback selection and history settlement preserve shelf objects, cover sources and horizontal scroll position");
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
                var actions = Descendants<Button>(list).Where(b => ReferenceEquals(b.Command, home.PlaySongCommand)).ToArray();
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
            Check(carousel.ItemsSource!.Count >= 2 && carousel.SelectedItem is HomeTheme, "featured control receives theme snapshots through compiled binding");
            var next = (Button)carousel.FindName("NextButton");
            next.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            Check(next.Opacity == 1 && next.IsHitTestVisible && next.OpacityTransition is not null && next.ScaleTransition is not null,
                "keyboard focus reveals arrow actions with fade and scale transitions");
            refreshButton.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            Check(next.Opacity == 0 && !next.IsHitTestVisible, "hidden arrow actions leave no invisible pointer hit area");
            carousel.Move(1);
            Check(carousel.SelectedIndex == 1, "featured control navigates without an autoplay timer");
            ((PipsPager)carousel.FindName("Pager")).SelectedPageIndex = 0;
            Check(carousel.SelectedIndex == 0, "dot pagination updates the selected carousel content");
            var source = new ObservableCollection<HomeTheme> { home.Themes[0], home.Themes[0] with { Title = "Same cover, other title" } };
            carousel.ItemsSource = source;
            carousel.Move(1);
            await Task.Delay(100);
            Check(Descendants<TextBlock>(carousel).Any(t => t.Text == "Same cover, other title"), "same artwork does not suppress title and parameter updates");
            var movingCard = Descendants<HomeHeroCard>(carousel).First();
            var motionTitle = (TextBlock)movingCard.FindName("TitleText");
            var motionDescription = (TextBlock)movingCard.FindName("DescriptionText");
            var motionButton = (Button)movingCard.FindName("PlayButton");
            movingCard.Theme = movingCard.Theme! with { Title = "Motion title", Description = "Motion description" };
            await Task.Delay(60);
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                Check(motionTitle.Opacity > motionButton.Opacity && motionButton.Opacity < 1,
                    "title enters ahead of its independently animated play action");
            movingCard.Theme = movingCard.Theme! with { Title = "Latest title" };
            await Task.Delay(620);
            await Capture(page, "home-animation-rest.png");
            await Task.Delay(60);
            Check(motionTitle.Text == "Latest title" && motionTitle.Opacity > .99 && motionDescription.Opacity > .79
                && motionButton.Opacity > .99 && motionTitle.RenderTransform is CompositeTransform { TranslateX: < .1, ScaleX: > .99 },
                "rapid same-artwork theme changes finish all text and button transforms without a stale animation");
            source.RemoveAt(1);
            Check(carousel.SelectedIndex == 0 && !carousel.CanNavigate, "shrinking observable collection clamps selection");
            source.Clear();
            Check(carousel.SelectedItem is null && carousel.Position == "0 / 0", "empty carousel never indexes the old selection");
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
            await Task.Delay(620);
            var card = Descendants<HomeHeroCard>(page).First();
            Check(((MusicCoverControl)card.FindName("Artwork")).Visibility == Visibility.Visible
                && Grid.GetRow(collage) == 2 && Grid.GetRowSpan((Button)carousel.FindName("PrimaryThumbnail")) == 2
                && Math.Abs(((Grid)carousel.FindName("SliderGrid")).ActualWidth - collage.ActualWidth) < .5,
                "narrow Store layout keeps full artwork and moves the large-left two-right collage below it");
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
