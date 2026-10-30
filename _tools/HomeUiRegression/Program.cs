using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
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
    private static async Task CloseFlyout(MenuFlyout flyout)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, object args) => closed.TrySetResult();
        flyout.Closed += OnClosed;
        try
        {
            flyout.Hide();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { flyout.Closed -= OnClosed; }
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
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "native-diagnostics.log"), "");
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Style/BtnStyle.xaml") });
            await database.CreateTableAsync<PlayListMusic>();
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
            await database.CreateTableAsync<Music>();
            await database.CreateTableAsync<MusicLyrics>();
            await database.CreateTableAsync<PendingMetadataWrite>();
            await data.Lyrics.InitializeAsync(path);
            var editorFactory = new FixtureEditorFactory(data, app);
            home = new(app, data, statistics, new HomeRecommendationService(), playback, folders, remote, NullLogger<HomeViewModel>.Instance, editorFactory);
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
            foreach (var song in songs)
            {
                WinUIMusicPlayer.Utils.ToolUtils.PrepareArtwork(song.Id);
                await database.InsertAsync(song);
            }
            bool offThread = false;
            home.PropertyChanged += (_, _) => offThread |= !DispatcherQueue.GetForCurrentThread().HasThreadAccess;
            app.Replace(songs);
            await Wait(() => home.HasContent && !home.IsLoading);
            Check(((ScrollViewer)page.FindName("ContentScroll")).Visibility == Visibility.Visible && !home.IsEmpty,
                "first imported batch replaces empty state before overall scanning completes");
            Check(home.Familiar.Count == 20 && ReferenceEquals(home.Songs[0].Music, app.FindById(home.Songs[0].Music.Id)),
                "real SQLite history and canonical music objects reach the shipping page");
            Check(!home.PlaySongCommand.CanExecute(home.Songs[0]), "play remains guarded until engine and lifecycle are ready");
            app.State.Lifecycle.TransitionTo(AppPhase.Initializing);
            app.State.Lifecycle.TransitionTo(AppPhase.Ready);
            app.IsPlaybackEngineReady = true;
            await Wait(() => Descendants<MusicCoverControl>(page).Any(c => c.DecodePixelSize == 800 && c.ArtworkColor.A == 255));
            var initialSongs = home.Songs;
            var initialThemes = home.Themes;
            var carousel = (FeaturedCarousel)page.FindName("Featured");
            carousel.AutoShuffle = false; // 手动导航断言需要确定时序，自动轮播单独验证。
            if (Environment.GetCommandLineArgs().Contains("--interactive"))
            {
                window.Title = "Home Carousel interaction fixture";
                window.AppWindow.Move(new Windows.Graphics.PointInt32(80, 80));
                void SaveState()
                {
                    var content = (ScrollViewer)page.FindName("ContentScroll");
                    var offsets = Descendants<RecommendationShelf>(page).Select(item => new
                    {
                        item.Name,
                        Offset = Descendants<ScrollView>(item).First().HorizontalOffset
                    });
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "interactive-state.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { Vertical = content.VerticalOffset, Shelves = offsets,
                            Preview = home.PreviewSong?.Music.Title }));
                }
                foreach (var scrolling in Descendants<ScrollViewer>(page)) scrolling.ViewChanged += (_, _) => SaveState();
                foreach (var scrolling in Descendants<ScrollView>(page)) scrolling.ViewChanged += (_, _) => SaveState();
                home.PropertyChanged += (_, _) => SaveState();
                SaveState();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                await closed.Task;
                window = null;
                return;
            }
            var imageShadows = Field<CarouselArtworkShadow[]>(carousel, "_thumbnailShadows")
                .Append(Field<CarouselArtworkShadow>((HomeHeroCard)page.FindName("HomeHero"), "_shadow")).ToArray();
            Check(imageShadows.Length == 4 && imageShadows.All(shadow => shadow.IsAttached),
                "all four artwork shadows attach outside the rounded card clips");
            Check(imageShadows.All(shadow => Field<Microsoft.UI.Composition.SpriteVisual>(shadow, "_visual").Offset.X == -12
                && Field<Microsoft.UI.Composition.CompositionLinearGradientBrush>(shadow, "_horizontalMask").ColorStops[0].Color.A == 0
                && Field<Microsoft.UI.Composition.CompositionLinearGradientBrush>(shadow, "_verticalMask").ColorStops[3].Color.A == 0),
                "compact artwork shadows fade fully transparent within a twelve-pixel gutter");
            CarouselLighting[] lights = [Field<CarouselLighting>(carousel, "_lighting")];
            Check(lights.All(l => l.IsAttached), "hero and collage share lighting within the two non-ambient light limit");
            var lightShade = (GradientStop)((HomeHeroCard)page.FindName("HomeHero")).FindName("ShadeStart");
            var lightTitle = (TextBlock)((HomeHeroCard)page.FindName("HomeHero")).FindName("TitleText");
            Check(lightShade.Color.A == 128 && lightShade.Color.R > 200 && lightShade.Color.R == lightShade.Color.G && lightShade.Color.G == lightShade.Color.B
                && lightTitle.Foreground is SolidColorBrush lightFg && lightFg.Color.R < 100,
                "light theme pairs a light scrim with dark hero text");
            var slider = (Grid)carousel.FindName("SliderGrid");
            var primary = (ContentPresenter)carousel.FindName("PrimaryThumbnail");
            var secondary = (ContentPresenter)carousel.FindName("SecondaryThumbnail");
            Check(slider.ActualWidth / slider.ActualHeight is > 1.16 and < 1.3
                && primary.ActualWidth / primary.ActualHeight is > 1.2 and < 1.65
                && secondary.ActualWidth / secondary.ActualHeight is > .8 and < 1.15,
                $"main and collage artwork use near-square rectangular proportions (slider {slider.ActualWidth:F0}x{slider.ActualHeight:F0}, primary {primary.ActualWidth:F0}x{primary.ActualHeight:F0}, secondary {secondary.ActualWidth:F0}x{secondary.ActualHeight:F0})");
            var contentScroll = (ScrollViewer)page.FindName("ContentScroll");
            contentScroll.ChangeView(null, 520, null, true);
            await Task.Delay(180);
            var shelf = (ItemsRepeater)((RecommendationShelf)page.FindName("SongsShelf")).FindName("Items");
            var shelfScroll = Descendants<ScrollView>((RecommendationShelf)page.FindName("SongsShelf")).First();
            shelfScroll.ScrollTo(140, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            await Task.Delay(100);
            foreach (var list in Descendants<RecommendationShelf>(page).Select(item => (ItemsRepeater)item.FindName("Items")))
            {
                list.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await Wait(() => Descendants<ContentControl>(list).Any(item => item.Tag is HomeSong));
                var cards = Descendants<ContentControl>(list).Where(item => item.Tag is HomeSong).ToArray();
                Check(cards.Length > 0 && !Descendants<Button>(list).Any(),
                    "shelf cards have no Button wrappers, cover play actions or footer buttons");
                Check(cards.All(item => FrameworkElementAutomationPeer.CreatePeerForElement(item)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider),
                    "song cards expose an accessible detail Invoke action without Button wrappers");
                Check(cards.All(item => item.Content is StackPanel panel && panel.Children.Count == 2
                    && panel.Children[0] is MusicCoverControl && panel.Children[1] is TextBlock),
                    "normal song cards contain only artwork and title");
            }
            foreach (string name in new[] { "PrimaryThumbnail", "SecondaryThumbnail", "TertiaryThumbnail" })
            {
                var tile = (ContentPresenter)carousel.FindName(name);
                Check(Descendants<HomeCarouselTile>(tile).Single().Song is not null && !Descendants<Button>(tile).Any(),
                    name + " preserves artwork and title without an invokable cover button");
            }
            Check(playback.LastId == 0, "loading and focusing song cards never starts playback");
            ((ScrollViewer)page.FindName("ContentScroll")).ChangeView(null, 520, null, true);
            shelfScroll.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            await Task.Delay(150);
            var cardSource = Descendants<ContentControl>(shelf).First(item => item.Tag is HomeSong);
            var selected = (HomeSong)cardSource.Tag;
            Check(cardSource.Focus(FocusState.Keyboard), "song card remains keyboard focusable");
            await Task.Delay(230);
            Check(home.PreviewSong is null, "keyboard focus leaves Enter available for full song properties instead of moving to preview playback");
            double detailEntryOffset = contentScroll.VerticalOffset;
            var details = (HomeMusicDetailControl)page.FindName("DetailView");
            carousel.AutoShuffle = true;
            app.IsPlaybackEngineReady = false;
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(cardSource).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => home.IsInDetailMode && !Field<bool>(page, "_detailTransitionPending"));
            Check(ReferenceEquals(home.DetailSong, selected) && ReferenceEquals(details.Song?.Music, selected.Music)
                && app.IsBackBtnEnable && playback.LastId == 0
                && ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), details.FindName("HeaderFocus"))
                && ((ScrollViewer)details.FindName("DetailScroll")).VerticalOffset == 0,
                "card Invoke focuses the fixed properties heading without playback or input scroll while the audio engine is not ready");
            app.IsPlaybackEngineReady = true;
            Check(Field<Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation?>(page, "_detailAnimation") is not null,
                "forward transition successfully starts a native connected cover animation");
            await Wait(() => !Field<bool>(page, "_detailTransitioning"));
            Check(!((ContentControl)page.FindName("BrowseRoot")).IsEnabled && !carousel.AutoShuffle,
                "property view disables hidden browse inputs and pauses autoplay while preserving the loaded shelves");
            Check(!home.OpenDetails(home.Songs.First(item => !ReferenceEquals(item, selected))) && ReferenceEquals(home.DetailSong, selected),
                "repeated detail entry cannot replace the song during a live transition");
            bool detailFavoriteBefore = selected.Music.IsFavorite;
            ((IInvokeProvider)new ButtonAutomationPeer((Button)details.FindName("FavoriteButton")).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => selected.Music.IsFavorite != detailFavoriteBefore);
            Check(ReferenceEquals(((Button)details.FindName("QueueButton")).CommandParameter, selected.Music),
                "full properties favorite and queue actions share the canonical track");
            string originalAuthor = selected.Music.Author;
            home.DetailEditor!.Metadata.Draft.Author = "Updated draft artist";
            await Task.Delay(30);
            Check(Descendants<TextBlock>(details).Any(item => item.Text == "Updated draft artist"),
                "the identity rail follows the current editable artist draft");
            home.DetailEditor!.Metadata.Draft.Author = originalAuthor;
            Check(details.Duration(TimeSpan.FromHours(27) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)) == "27:02:03"
                && details.Number(0) == "—", "property formatting preserves long durations and distinguishes unknown numeric tags");
            var detailPlayButton = (Button)details.FindName("PlayButton");
            ((IInvokeProvider)new ButtonAutomationPeer(detailPlayButton).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => playback.LastId == selected.Music.Id);
            Check(app.SequentialPlayingList.Select(m => m.Id).SequenceEqual(selected.Queue.Select(m => m.Id)),
                "full properties play action uses the original source shelf queue");
            var detailPlaylist = (MenuFlyout)((Button)details.FindName("PlaylistButton")).Flyout;
            detailPlaylist.ShowAt((Button)details.FindName("PlaylistButton"));
            await Task.Delay(80);
            Check(detailPlaylist.Items.Count == 1 && !detailPlaylist.Items[0].IsEnabled,
                "full properties playlist menu resolves the detail song while preview is closed");
            await CloseFlyout(detailPlaylist);
            app.AllPlayList.Add(new PlayList { Id = 42, Name = "Properties playlist" });
            detailPlaylist.ShowAt((Button)details.FindName("PlaylistButton"));
            await Task.Delay(80);
            Check(detailPlaylist.Items[0] is MenuFlyoutItem { CommandParameter: HomePlaylistTarget { PlaylistId: 42 } detailTarget }
                && detailTarget.MusicId == selected.Music.Id,
                "full properties playlist menu captures the correct song without depending on hover state");
            await CloseFlyout(detailPlaylist);
            data.FailPlaylistAdd = true;
            await home.AddToPlaylistCommand.ExecuteAsync(new HomePlaylistTarget(42, selected.Music.Id));
            Check(((InfoBar)details.FindName("ErrorInfo")).IsOpen,
                "playlist failures remain visible inside the properties view while browse is hidden");
            data.FailPlaylistAdd = false;
            await home.AddToPlaylistCommand.ExecuteAsync(new HomePlaylistTarget(42, selected.Music.Id));
            app.AllPlayList.Clear();
            await database.ExecuteAsync("DELETE FROM PlayListMusic WHERE PlayListId = 42");
            await Capture(page, "home-song-properties.png");
            page.CollapseDetail(); // 与 MainPage.HandleBackNavigation 使用同一返回入口。
            await Wait(() => !home.IsInDetailMode && !Field<bool>(page, "_detailTransitionPending"));
            Check(Field<Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation?>(page, "_detailAnimation")?.Configuration
                is Microsoft.UI.Xaml.Media.Animation.DirectConnectedAnimationConfiguration && home.HasDetails,
                "back starts the native direct animation and retains the source cover until completion");
            await Wait(() => !home.HasDetails);
            await Task.Delay(150);
            Check(Math.Abs(contentScroll.VerticalOffset - detailEntryOffset) < 1 && !app.IsBackBtnEnable
                && ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), cardSource) && carousel.AutoShuffle,
                "return restores the original card focus and vertical position without reopening hover preview");
            Check(((Image)((MusicCoverControl)details.FindName("Artwork")).FindName("CoverImage")).Source is null,
                "completing back releases the full properties cover consumer");
            carousel.AutoShuffle = false;
            playback.LastId = 0;
            ((Button)page.FindName("RefreshButton")).Focus(FocusState.Programmatic);
            var preview = (HomeSongPreview)page.FindName("SongPreview");
            var previewSource = (FrameworkElement)cardSource;
            typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [previewSource]);
            await Wait(() => home.PreviewSong is not null && preview.Visibility == Visibility.Visible);
            await Task.Delay(300);
            Check(ReferenceEquals(home.PreviewSong, selected) && ReferenceEquals(preview.Song, selected)
                && Descendants<TextBlock>(preview).Any(t => t.Text == selected.Music.Author)
                && Descendants<TextBlock>(preview).Any(t => t.Text == selected.Music.Album),
                "shared detail overlay binds the canonical song, artist and album after hover delay");
            var overlayParent = (Canvas)page.FindName("PreviewLayer");
            Check(ReferenceEquals(preview.Parent, overlayParent) && Canvas.GetLeft(preview) >= 0 && Canvas.GetTop(preview) >= 0
                && Canvas.GetLeft(preview) + preview.ActualWidth <= overlayParent.ActualWidth
                && Canvas.GetTop(preview) + preview.ActualHeight <= overlayParent.ActualHeight,
                "detail overlay stays inside the page without changing shelf layout");
            Check(ElementCompositionPreview.GetElementVisual(preview).Clip is Microsoft.UI.Composition.InsetClip,
                "detail reveal uses a native composition clip outside the scrolling list");
            await Capture(page, "home-detail.png");
            double previewEntryOffset = contentScroll.VerticalOffset;
            typeof(HomePage).GetMethod("OpenSongDetails", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page,
                [previewSource, selected, (MusicCoverControl)preview.FindName("Artwork"), false]);
            await Wait(() => home.IsInDetailMode && !Field<bool>(page, "_detailTransitionPending"));
            Check(home.PreviewSong is null && Field<Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation?>(page, "_detailAnimation") is not null,
                "opening from the hover artwork hands off a native connected snapshot and dismisses preview");
            await Wait(() => !Field<bool>(page, "_detailTransitioning"));
            page.CollapseDetail();
            await Wait(() => !home.HasDetails);
            await Task.Delay(180);
            Check(Math.Abs(contentScroll.VerticalOffset - previewEntryOffset) < 1 && ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), page.FindName("RefreshButton")),
                "pointer entry restores a fixed header focus target without bringing a partially visible card into view");
            typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [previewSource]);
            await Wait(() => home.PreviewSong is not null);
            await Task.Delay(260);
            var favorite = Descendants<Button>(preview).First(b => ReferenceEquals(b.Command, WinUIMusicPlayer.Services.MusicCommands.UpdateFavouriteCommand));
            Check(Descendants<FontIcon>(favorite).Single().Foreground is SolidColorBrush heart && heart.Color == Microsoft.UI.Colors.Red,
                "favorite heart follows the existing red icon convention");
            var queueButton = Descendants<Button>(preview).First(b => ReferenceEquals(b.Command, WinUIMusicPlayer.Services.MusicCommands.AddToPlayListCommand));
            Check(ToolTipService.GetToolTip(queueButton)?.ToString() == "Add to current playback queue", "queue plus button explains its purpose on hover");
            var refreshButton = (Button)page.FindName("RefreshButton");
            Check(ReferenceEquals(refreshButton.Style, Resources["MenuButtonStyle"]), "refresh action reuses the production animated MenuButtonStyle");
            Check(Descendants<Button>(preview).All(item => ReferenceEquals(item.Style.BasedOn, Resources["MenuButtonStyle"])
                    && item.Padding.Left == 12 && item.Padding.Top == 10 && item.BorderThickness.Left == 1),
                "all four detail actions derive menu animations with padded bordered button surfaces");
            var actions = (StackPanel)preview.FindName("Actions");
            Check(actions.Orientation == Orientation.Horizontal && actions.Spacing == 6
                && actions.ActualWidth < 220 && Descendants<Button>(preview).All(item => item.ActualWidth >= 44),
                "detail actions retain compact six-pixel gaps and at least 44-pixel hit targets independent of panel width");
            var animationRoot = Descendants<Grid>(favorite).First(item => item.Name == "Root");
            VisualStateManager.GoToState(favorite, "Normal", false);
            await Task.Delay(20);
            VisualStateManager.GoToState(favorite, "PointerOver", false);
            await Task.Delay(20);
            VisualStateManager.GoToState(favorite, "Pressed", false);
            await Task.Delay(30);
            Check(ElementCompositionPreview.GetElementVisual(animationRoot).TryGetAnimationController("Scale") is not null,
                "detail action press runs the actual MenuButtonStyle composition scale animation");
            VisualStateManager.GoToState(favorite, "Normal", true);
            bool oldFavorite = selected.Music.IsFavorite;
            ((IInvokeProvider)new ButtonAutomationPeer(favorite).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => selected.Music.IsFavorite != oldFavorite);
            Check(Descendants<FontIcon>(favorite).Single().Glyph == WinUIMusicPlayer.Utils.BindUtils.FavouriteGlyphConverter(selected.Music.IsFavorite),
                "detail favorite command updates the canonical song and compiled icon binding");
            var detailPlay = (Button)preview.FindName("PlayButton");
            playback.LastId = 0;
            ((IInvokeProvider)new ButtonAutomationPeer(detailPlay).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => playback.LastId == selected.Music.Id);
            Check(app.SequentialPlayingList.Select(m => m.Id).SequenceEqual(selected.Queue.Select(m => m.Id)),
                "detail play preserves the explicit source shelf queue");
            var playlistButton = (Button)preview.FindName("PlaylistButton");
            var playlistMenu = (MenuFlyout)playlistButton.Flyout;
            playlistMenu.ShowAt(playlistButton);
            await Wait(() => preview.IsPlaylistMenuOpen);
            Check(playlistMenu.Items.Count == 1 && !playlistMenu.Items[0].IsEnabled,
                "empty playlist menu displays one disabled localized placeholder");
            playlistMenu.Hide();
            await Wait(() => !preview.IsPlaylistMenuOpen);
            app.AllPlayList.Add(new PlayList { Id = 1, Name = "First playlist" });
            app.AllPlayList.Add(new PlayList { Id = 2, Name = "Second playlist" });
            playlistMenu.ShowAt(playlistButton);
            await Wait(() => preview.IsPlaylistMenuOpen && playlistMenu.Items.Count == 2);
            typeof(HomePage).GetMethod("Song_PointerExited", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [previewSource, null]);
            typeof(HomePage).GetMethod("Preview_PointerExited", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [null, null]);
            await Task.Delay(400);
            Check(ReferenceEquals(home.PreviewSong, selected) && preview.Visibility == Visibility.Visible,
                "moving into the playlist flyout pins its owning detail panel");
            var firstMenuItem = (MenuFlyoutItem)playlistMenu.Items[0];
            Check(firstMenuItem.CommandParameter is HomePlaylistTarget { PlaylistId: 1 } target && target.MusicId == selected.Music.Id,
                "playlist menu captures the source song and destination identifiers");
            home.ShowPreview(home.Songs.First(song => !ReferenceEquals(song, selected)));
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(firstMenuItem).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => data.PlaylistAddsStarted > 0);
            await Wait(() => !home.AddToPlaylistCommand.IsRunning);
            Check(await database.ExecuteScalarAsync<int>("SELECT MusicId FROM PlayListMusic LIMIT 1") == selected.Music.Id,
                "native menu invocation writes the captured song even if the preview selection changes");
            playlistMenu.Hide();
            await Wait(() => !preview.IsPlaylistMenuOpen);
            home.ShowPreview(selected);
            typeof(HomePage).GetMethod("Preview_PointerEntered", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [null, null]);
            var playlistGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.PlaylistGate = playlistGate.Task;
            int addsBefore = data.PlaylistAddsStarted;
            var firstWrite = home.AddToPlaylistCommand.ExecuteAsync(new HomePlaylistTarget(1, selected.Music.Id));
            var secondWrite = home.AddToPlaylistCommand.ExecuteAsync(new HomePlaylistTarget(2, selected.Music.Id));
            await Task.Delay(40);
            Check(data.PlaylistAddsStarted == addsBefore + 1 && !secondWrite.IsCompleted,
                "rapid additions are queued without overlapping database writes or dropping a target playlist");
            playlistGate.SetResult();
            await Task.WhenAll(firstWrite, secondWrite);
            data.PlaylistGate = Task.CompletedTask;
            Check(await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PlayListMusic WHERE MusicId = ?", selected.Music.Id) == 2,
                "queued additions preserve both playlist destinations and avoid duplicate membership");
            data.FailPlaylistAdd = true;
            await home.AddToPlaylistCommand.ExecuteAsync(new HomePlaylistTarget(2, selected.Music.Id));
            Check(home.ErrorMessage == WinUIMusicPlayer.Utils.ToolUtils.GetString("HomePlaylistAddFailed"),
                "playlist failure is observed and published as localized page feedback");
            data.FailPlaylistAdd = false;
            await home.AddToPlaylistCommand.ExecuteAsync(new HomePlaylistTarget(2, selected.Music.Id));
            Check(home.ErrorMessage.Length == 0, "retry clears the playlist error after a successful write");
            home.DismissPreview();
            ((ScrollViewer)page.FindName("ContentScroll")).ChangeView(null, 0, null, true);
            await Task.Delay(150);
            foreach (string name in new[] { "PrimaryThumbnail", "SecondaryThumbnail", "TertiaryThumbnail" })
            {
                var tileHost = (ContentPresenter)carousel.FindName(name);
                var tile = Descendants<HomeCarouselTile>(tileHost).Single();
                var tileSong = tile.Song!;
                typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [tile]);
                await Wait(() => ReferenceEquals(home.PreviewSong, tileSong) && preview.Visibility == Visibility.Visible);
                Check(preview.ActualWidth >= tile.ActualWidth + 40,
                    name + " detail expands wider than its original tile");
                Check(ReferenceEquals(preview.Song, tileSong)
                    && Math.Abs(((Grid)preview.FindName("CoverHost")).Height / (preview.Width - 2) - tile.ActualHeight / tile.ActualWidth) < .02,
                    name + " opens shared details from its own artwork bounds while preserving its aspect ratio");
                playback.LastId = 0;
                ((IInvokeProvider)new ButtonAutomationPeer((Button)preview.FindName("PlayButton")).GetPattern(PatternInterface.Invoke)).Invoke();
                await Wait(() => playback.LastId == tileSong.Music.Id);
                Check(app.SequentialPlayingList.Select(m => m.Id).SequenceEqual(tileSong.Queue.Select(m => m.Id)),
                    name + " plays only through its detail button with the original recommendation queue");
                home.DismissPreview();
            }
            foreach (string name in new[] { "PrimaryThumbnail", "SecondaryThumbnail", "TertiaryThumbnail" })
            {
                var host = Descendants<HomeSongItem>((ContentPresenter)carousel.FindName(name)).Single();
                ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(host).GetPattern(PatternInterface.Invoke)).Invoke();
                await Wait(() => home.IsInDetailMode && !Field<bool>(page, "_detailTransitionPending"));
                Check(ReferenceEquals(home.DetailSong, host.Tag) && Field<Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation?>(page, "_detailAnimation") is not null,
                    name + " opens properties through its accessible card host and a real connected animation");
                page.CollapseDetail();
                await Wait(() => !home.HasDetails);
                Check(!Field<bool>(page, "_detailTransitioning"), name + " permits back during the forward animation without a stuck snapshot");
            }
            ((ScrollViewer)page.FindName("ContentScroll")).ChangeView(null, 520, null, true);
            await Task.Delay(150);
            typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [previewSource]);
            await Wait(() => ReferenceEquals(home.PreviewSong, selected));
            await Task.Delay(260);
            Check(ReferenceEquals(queueButton.CommandParameter, selected.Music), "detail queue command receives the canonical song");
            var exitStarted = System.Diagnostics.Stopwatch.StartNew();
            typeof(HomePage).GetMethod("BeginPreviewClose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
            Check(ReferenceEquals(home.PreviewSong, selected) && preview.Visibility == Visibility.Visible
                && ElementCompositionPreview.GetElementVisual(preview).TryGetAnimationController("Scale") is not null
                && ElementCompositionPreview.GetElementVisual(preview).Clip!.TryGetAnimationController("BottomInset") is not null,
                "exit keeps artwork alive while shrinking and retracting details on native composition animations");
            await Wait(() => home.PreviewSong is null);
            Check(preview.Visibility == Visibility.Collapsed && exitStarted.ElapsedMilliseconds >= 180,
                "native batch completion hides the preview only after the 200ms exit rather than a synchronous clear");
            typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [previewSource]);
            await Wait(() => home.PreviewSong is not null);
            await Task.Delay(260);
            typeof(HomePage).GetMethod("BeginPreviewClose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
            await Task.Delay(60);
            typeof(HomePage).GetMethod("Preview_PointerEntered", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [null, null]);
            await Task.Delay(260);
            Check(ReferenceEquals(home.PreviewSong, selected) && preview.Visibility == Visibility.Visible,
                "reentering a closing card cancels its completion callback and restores the panel");
            var adjacent = Descendants<ContentControl>(shelf).First(item => item.Tag is HomeSong other && !ReferenceEquals(other, selected));
            var adjacentSong = (HomeSong)adjacent.Tag;
            typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [adjacent]);
            await Task.Delay(70);
            Check(ReferenceEquals(home.PreviewSong, selected) && preview.Visibility == Visibility.Visible,
                "moving directly to an adjacent card preserves the old preview during its exit");
            await Wait(() => ReferenceEquals(home.PreviewSong, adjacentSong));
            Check(ReferenceEquals(preview.Song, adjacentSong), "adjacent card enters after the previous exit completes");
            var pagedShelf = (RecommendationShelf)page.FindName("SongsShelf");
            Check(shelfScroll.HorizontalScrollBarVisibility == ScrollingScrollBarVisibility.Hidden,
                "shelf hides its scrollbar and exposes boundary-aware circular paging buttons");
            Check(shelfScroll.HorizontalScrollMode == ScrollingScrollMode.Disabled
                && shelfScroll.VerticalScrollMode == ScrollingScrollMode.Disabled
                && shelfScroll.IgnoredInputKinds.HasFlag(ScrollingInputKinds.MouseWheel),
                "shelves disable native pan gestures and ignore both wheel axes without manually changing page offsets");
            Check(Descendants<Canvas>((ScrollViewer)page.FindName("ContentScroll")).Contains(overlayParent),
                "detail overlay shares the page scroll ancestor so wheel input over details can bubble natively");
            double beforePaging = shelfScroll.HorizontalOffset;
            pagedShelf.Move(1);
            await Wait(() => shelfScroll.HorizontalOffset > beforePaging + 100);
            Check(home.PreviewSong is null && preview.Visibility == Visibility.Collapsed,
                "paging dismisses the overlay before its source card moves");
            pagedShelf.Move(1);
            pagedShelf.Move(1);
            pagedShelf.Move(1);
            await Wait(() => !((Button)pagedShelf.FindName("NextButton")).IsEnabled);
            Check(!((Button)pagedShelf.FindName("NextButton")).IsEnabled,
                "rapid next requests accumulate to the last page and disable the boundary button");
            pagedShelf.Move(-1);
            await Wait(() => shelfScroll.HorizontalOffset < shelfScroll.ScrollableWidth - 100);
            Check(((Button)pagedShelf.FindName("NextButton")).IsEnabled,
                "backward paging re-enables the next button");
            shelfScroll.ScrollTo(172, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            await Wait(() => Math.Abs(shelfScroll.HorizontalOffset - 172) < 1);
            double expectedNext = 172 + Math.Floor(shelfScroll.ViewportWidth / 172) * 172;
            pagedShelf.Move(1);
            await Wait(() => Math.Abs(shelfScroll.HorizontalOffset - expectedNext) < 1);
            Check(Math.Abs(shelfScroll.HorizontalOffset - expectedNext) < 1,
                "paging after a native focus or external scroll starts at the current offset instead of an obsolete button target");
            shelfScroll.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            home.ShowPreview(selected);
            home.DismissPreview();
            Check(((Image)((MusicCoverControl)preview.FindName("Artwork")).FindName("CoverImage")).Source is null,
                "closing details releases the independently owned cover source");
            ((Button)page.FindName("RefreshButton")).Focus(FocusState.Programmatic);
            ((ScrollViewer)page.FindName("ContentScroll")).ChangeView(null, 0, null, true);
            await Task.Delay(120);
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
            Check(ambient.Targets.Count == 4 && hover.Targets.Count == 4 && ripple.Targets.Count == 4
                && hover.CoordinateSpace == ElementCompositionPreview.GetElementVisual((Grid)carousel.FindName("LayoutGrid"))
                && Math.Abs(hover.Offset.X - slider.ActualWidth * .7 - 70) < .1 && hover.Direction.Y > 0 && hover.Direction.X < 0,
                "directional hover light intersects the pointer from above and stays scoped to the carousel");
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
            Check(Grid.GetColumn(collage) == 1 && Grid.GetColumnSpan((ContentPresenter)carousel.FindName("PrimaryThumbnail")) == 2
                && Grid.GetRow((ContentPresenter)carousel.FindName("SecondaryThumbnail")) == 1,
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
            Check(next.Visibility == Visibility.Visible && next.Opacity > .99 && next.OpacityTransition is not null && next.Background is AcrylicBrush,
                "arrow actions use acrylic material and native opacity transitions with keyboard access");
            refreshButton.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            Check(next.Opacity < .01 && !next.IsHitTestVisible && !next.IsTabStop, "faded arrow actions leave no invisible pointer hit or tab area");
            next.Focus(FocusState.Pointer);
            typeof(FeaturedCarousel).GetMethod("Carousel_PointerExited", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(carousel, [null, null]);
            await Task.Delay(240);
            Check(next.Opacity < .01 && !next.IsHitTestVisible, "pointer focus from an arrow click cannot keep navigation visible after mouse exit");
            refreshButton.Focus(FocusState.Programmatic);
            var countdown = (CarouselCountdown)carousel.FindName("Countdown");
            carousel.ShuffleDuration = TimeSpan.FromMilliseconds(800);
            carousel.AutoShuffle = true;
            await Task.Delay(220);
            Check(countdown.Visibility == Visibility.Visible && countdown.Remaining is > .4 and < .95,
                "lower-left countdown pie animates its remaining autoplay fraction");
            typeof(FeaturedCarousel).GetMethod("Carousel_PointerEntered", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(carousel, [null, null]);
            double pausedFraction = countdown.Remaining;
            await Task.Delay(250);
            Check(Math.Abs(pausedFraction - countdown.Remaining) < .01,
                "hover pauses both countdown drawing and the automatic page timer");
            typeof(FeaturedCarousel).GetMethod("Carousel_PointerExited", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(carousel, [null, null]);
            await Task.Delay(100);
            Check(countdown.Remaining < pausedFraction - .05,
                "leaving resumes the same remaining countdown instead of starting a fresh full disc");
            carousel.AutoShuffle = false;
            Check(countdown.Visibility == Visibility.Collapsed, "disabled autoplay hides and stops its countdown");
            ((PipsPager)carousel.FindName("Pager")).SelectedPageIndex = 0;
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
            var unloadHost = Descendants<HomeSongItem>((ContentPresenter)carousel.FindName("PrimaryThumbnail")).Single();
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(unloadHost).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => home.IsInDetailMode);
            lights[0].Pulse(new Vector2(80, 80));
            window.Content = new Grid();
            await Task.Delay(140);
            Check(WinUIMusicPlayer.Utils.ToolUtils.RawActive == 0 && !home.PlaySongCommand.CanExecute(selected),
                "unloading cancels cover consumption and disables page playback");
            Check(!home.HasDetails && !app.IsBackBtnEnable && Field<Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation?>(page, "_detailAnimation") is null,
                "unloading during detail entry cancels native animation and clears navigation state before a late callback");
            Check(lights.All(l => !l.IsAttached), "unloading releases native lights and an in-flight press animation");
            Check(imageShadows.All(shadow => !shadow.IsAttached), "unloading releases all live image surfaces and effect brushes");
            window.Content = page;
            await Task.Delay(400);
            Check(home.HasContent, "same-day page reentry restores recommendations after deferred listening updates");
            Check(lights.All(l => l.IsAttached), "page reentry recreates lighting without duplicate subscriptions");
            home.RefreshCommand.Execute(null);
            await Task.Delay(400);
            Check(((FeaturedCarousel)page.FindName("Featured")).ItemsSource!.Count > 0 && home.PlaySongCommand.CanExecute(home.Songs[0]),
                "reloaded page restarts compiled tracking and commands");
            await EditorCoverChecks.RunAsync(data, app, Check);
            // Exercise the actual page fields and production editor state machines against SQLite.
            var editCard = Descendants<HomeSongItem>((ContentPresenter)carousel.FindName("PrimaryThumbnail")).Single();
            var editMusic = ((HomeSong)editCard.Tag).Music;
            async Task<MusicDetailsEditorViewModel> OpenEditorAsync()
            {
                ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(editCard).GetPattern(PatternInterface.Invoke)).Invoke();
                await Wait(() => home.IsInDetailMode && !Field<bool>(page, "_detailTransitioning")
                    && home.DetailEditor is { IsLoading: false } current && current.Lyrics.CanSave);
                return home.DetailEditor!;
            }
            var editSession = await OpenEditorAsync();
            Check(((Grid)details.FindName("DetailRoot")).Background is SolidColorBrush { Color.A: 0 }
                && !Descendants<FontIcon>(details).Any(icon => icon.Glyph == "\uE72B"),
                "properties root stays transparent and has no duplicate back arrow");
            var parentColor = Microsoft.UI.ColorHelper.FromArgb(255, 22, 75, 120);
            page.Background = new SolidColorBrush(parentColor);
            await Task.Delay(80);
            var transparentFrame = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await transparentFrame.RenderAsync(page);
            var transparentPixels = (await transparentFrame.GetPixelsAsync()).ToArray();
            int transparentSample = ((int)(20 * transparentFrame.PixelHeight / page.ActualHeight) * transparentFrame.PixelWidth
                + (int)((page.ActualWidth - 40) * transparentFrame.PixelWidth / page.ActualWidth)) * 4;
            Check(transparentPixels[transparentSample] == parentColor.B && transparentPixels[transparentSample + 1] == parentColor.G
                && transparentPixels[transparentSample + 2] == parentColor.R,
                "rendered blank area shows the parent backdrop color instead of an opaque property page rectangle");
            page.Background = new SolidColorBrush(page.ActualTheme == ElementTheme.Dark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
            var titleInput = (TextBox)details.FindName("TitleInput");
            var composerInput = (TextBox)details.FindName("ComposerInput");
            var originalInput = (TextBox)details.FindName("OriginalLyricsInput");
            var translationInput = (TextBox)details.FindName("TranslationLyricsInput");
            var pronunciationInput = (TextBox)details.FindName("PronunciationLyricsInput");
            string canonicalTitle = editMusic.Title;
            titleInput.Text = "Edited properties title";
            composerInput.Text = "Edited composer";
            originalInput.Text = "[00:01.000]Original from property control";
            translationInput.Text = "[00:01.000]Translation from property control";
            pronunciationInput.Text = "[00:01.000]Pronunciation from property control";
            await Task.Delay(30);
            Check(editSession.Metadata.Draft.Title == titleInput.Text && editSession.Metadata.Draft.Composer == composerInput.Text
                && editSession.Lyrics.OriginalText == originalInput.Text && editSession.Lyrics.TranslationText == translationInput.Text
                && editSession.Lyrics.PronunciationText == pronunciationInput.Text && editMusic.Title == canonicalTitle,
                "compiled two-way fields edit independent metadata and all three lyric drafts without mutating the canonical song");
            await Task.Delay(450); // Wait for the native Pivot reveal before taking visual evidence.
            await Capture(page, "home-song-editor-tags.png");
            ((Pivot)details.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Task.Delay(450);
            await Capture(page, "home-song-editor-lyrics.png");
            data.FailEditorSave = true;
            await editSession.SaveDatabaseCommand.ExecuteAsync(null);
            Check(home.IsInDetailMode && editMusic.Title == canonicalTitle && editSession.Lyrics.Error.Length > 0
                && await database.ExecuteScalarAsync<string>("SELECT Title FROM Music WHERE Id=?", editMusic.Id) == canonicalTitle,
                "save failure preserves the draft and canonical database values and keeps the property editor open");
            data.FailEditorSave = false;
            var editingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.EditorSaveGate = editingGate.Task;
            int editingSaves = data.EditorSavesStarted;
            var editingSave = editSession.SaveDatabaseCommand.ExecuteAsync(null);
            await Wait(() => data.EditorSavesStarted > editingSaves);
            titleInput.Text = "Newer draft while saving";
            originalInput.Text = "[00:01.000]Newer original while saving";
            editingGate.SetResult();
            await editingSave;
            data.EditorSaveGate = Task.CompletedTask;
            Check(home.IsInDetailMode && editMusic.Title == "Edited properties title" && editSession.Metadata.Draft.Title == "Newer draft while saving"
                && editSession.Lyrics.OriginalText.Contains("Newer original"),
                "a committed snapshot publishes correctly while newer metadata and lyric edits keep the editor open");
            ((IInvokeProvider)new ButtonAutomationPeer((Button)details.FindName("SaveDatabaseButton")).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => !home.HasDetails);
            var storedLyrics = await data.Lyrics.GetAsync(editMusic.Id);
            Check(editMusic.Title == "Newer draft while saving" && editMusic.Composer == "Edited composer"
                && storedLyrics.Document.Original.Content.Contains("Newer original")
                && storedLyrics.Document.TranslationLrc!.Contains("Translation from property control")
                && storedLyrics.Document.PronunciationLrc!.Contains("Pronunciation from property control"),
                "saving from the native button commits tags and original/translation/pronunciation together and returns through navigation");
            await editSession.StopAsync();
            Check(editSession.CoverPreview is null && !editSession.SaveDatabaseCommand.CanExecute(null),
                "closed editor releases staged cover and disables retained commands");
            int retiredSaveCount = data.EditorSavesStarted;
            await editSession.SaveDatabaseCommand.ExecuteAsync(null);
            await editSession.SaveFileCommand.ExecuteAsync(null);
            await editSession.LoadCommand.ExecuteAsync(null);
            Check(data.EditorSavesStarted == retiredSaveCount && !editSession.IsLoading,
                "retained command execution guards reject saves and loads after the editor stops");
            editSession = await OpenEditorAsync();
            Check(editSession.Metadata.Draft.Title == editMusic.Title && editSession.Lyrics.OriginalText.Contains("Newer original"),
                "reopening properties reloads the committed metadata and lyric version");
            foreach (var exportPath in new[] { Path.ChangeExtension(editMusic.Path, ".lrc"),
                Path.Combine(Path.GetDirectoryName(editMusic.Path)!, Path.GetFileNameWithoutExtension(editMusic.Path) + "_Translated.lrc"),
                Path.Combine(Path.GetDirectoryName(editMusic.Path)!, Path.GetFileNameWithoutExtension(editMusic.Path) + "_Pronunciation.lrc") })
                if (File.Exists(exportPath)) File.Delete(exportPath);
            ((Pivot)details.FindName("WorkspaceTabs")).SelectedIndex = 1;
            var toolsButton = (Button)details.FindName("LyricsToolsButton");
            var toolsMenu = (MenuFlyout)toolsButton.Flyout;
            toolsMenu.ShowAt(toolsButton);
            await Wait(() => toolsMenu.Items.OfType<MenuFlyoutItem>().Any(item => ReferenceEquals(item.Command, editSession.Lyrics.ExportCommand)));
            var exportItem = toolsMenu.Items.OfType<MenuFlyoutItem>().First(item => ReferenceEquals(item.Command, editSession.Lyrics.ExportCommand));
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(exportItem).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => File.Exists(Path.ChangeExtension(editMusic.Path, ".lrc")));
            Check(File.ReadAllText(Path.ChangeExtension(editMusic.Path, ".lrc")).Contains("Newer original")
                && File.Exists(Path.Combine(Path.GetDirectoryName(editMusic.Path)!, Path.GetFileNameWithoutExtension(editMusic.Path) + "_Translated.lrc"))
                && File.Exists(Path.Combine(Path.GetDirectoryName(editMusic.Path)!, Path.GetFileNameWithoutExtension(editMusic.Path) + "_Pronunciation.lrc")),
                "the shared lyrics export action writes original, translation and pronunciation sidecars");
            await Wait(() => !editSession.Lyrics.IsBusy);
            var unchangedCover = editSession.CoverPreview;
            await editSession.ChooseCoverCommand.ExecuteAsync(null);
            Check(ReferenceEquals(unchangedCover, editSession.CoverPreview), "cancelling the cover picker preserves the staged cover");
            await editSession.FindCoverCommand.ExecuteAsync(null);
            Check(editSession.CoverPreview is not null && ReferenceEquals(((Image)details.FindName("CoverPreviewImage")).Source, editSession.CoverPreview),
                "cover source operations update the actual staged image without replacing the canonical cover");
            await editSession.ExportCoverCommand.ExecuteAsync(null);
            Check(new FileInfo(Path.Combine(AppContext.BaseDirectory, "exported-cover.bmp")).Length > 100,
                "the cover export action uses the current staged image bytes");
            ((TextBox)details.FindName("AlbumInput")).Text = "Queued album edit";
            await editSession.SaveFileCommand.ExecuteAsync(null);
            await Wait(() => !home.HasDetails);
            var pendingEdit = await database.FindAsync<PendingMetadataWrite>(editMusic.Path);
            Check(pendingEdit is { Cover.Length: > 0, Album: "Queued album edit" }
                && pendingEdit.Lyrics.Contains("Newer original") && pendingEdit.TranslationLrc!.Contains("Translation from property control")
                && pendingEdit.PronunciationLrc!.Contains("Pronunciation from property control"),
                "file-save command queues the same cover, metadata and three lyric fields as the property window");
            await editSession.StopAsync();
            editSession = await OpenEditorAsync();
            ((TextBox)details.FindName("TitleInput")).Text = "Unsaved draft to discard";
            ((TextBox)details.FindName("OriginalLyricsInput")).Text = "[00:03.000]Unsaved lyrics";
            page.CollapseDetail();
            await Wait(() => !home.HasDetails);
            await editSession.StopAsync();
            editSession = await OpenEditorAsync();
            Check(editSession.Metadata.Draft.Title == editMusic.Title && !editSession.Lyrics.OriginalText.Contains("Unsaved"),
                "navigation back discards unsaved drafts before another editor opens");
            var abandonedEditor = editSession;
            var abandonedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.EditorSaveGate = abandonedGate.Task;
            int abandonedSaves = data.EditorSavesStarted;
            ((TextBox)details.FindName("TitleInput")).Text = "Cancelled before commit";
            var abandonedSave = abandonedEditor.SaveDatabaseCommand.ExecuteAsync(null);
            await Wait(() => data.EditorSavesStarted > abandonedSaves);
            await abandonedEditor.SaveDatabaseCommand.ExecuteAsync(null); // A rejected duplicate must not replace the accepted task used by stop.
            page.CollapseDetail();
            await Wait(() => !home.HasDetails);
            var abandonedStop = abandonedEditor.StopAsync();
            Check(!abandonedStop.IsCompleted, "editor stop waits for a real in-flight save boundary after navigation cancels it");
            abandonedGate.SetResult();
            await Task.WhenAll(abandonedStop, abandonedSave);
            data.EditorSaveGate = Task.CompletedTask;
            Check(editMusic.Title != "Cancelled before commit" &&
                await database.ExecuteScalarAsync<string>("SELECT Title FROM Music WHERE Id=?", editMusic.Id) != "Cancelled before commit",
                "cancellation before commit cannot publish an abandoned draft");
            int originalSourceId = editMusic.SourceId;
            editMusic.SourceId = 1;
            editSession = await OpenEditorAsync();
            Check(((TextBox)details.FindName("TitleInput")).IsReadOnly && !((NumberBox)details.FindName("YearInput")).IsEnabled
                && !editSession.SaveFileCommand.CanExecute(null) && !editSession.ChooseCoverCommand.CanExecute(null)
                && !editSession.Lyrics.ExportCommand.CanExecute(null) && editSession.SaveDatabaseCommand.CanExecute(null),
                "remote properties keep file tags read-only while retaining local database lyrics editing");
            int remoteSavesBefore = data.EditorSavesStarted;
            await editSession.SaveFileCommand.ExecuteAsync(null);
            await editSession.ChooseCoverCommand.ExecuteAsync(null);
            await editSession.ReadEmbeddedCommand.ExecuteAsync(null);
            Check(data.EditorSavesStarted == remoteSavesBefore && editSession.Lyrics.Error.Length == 0,
                "direct command execution cannot bypass remote file-write and embedded-read guards");
            ((TextBox)details.FindName("OriginalLyricsInput")).Text = "[00:04.000]Remote database lyrics";
            await editSession.SaveDatabaseCommand.ExecuteAsync(null);
            await Wait(() => !home.HasDetails);
            Check((await data.Lyrics.GetAsync(editMusic.Id)).Document.Original.Content.Contains("Remote database lyrics"),
                "remote lyric edits persist through the shared local database path");
            await editSession.StopAsync();
            editMusic.SourceId = originalSourceId;
            await database.ExecuteAsync("UPDATE MusicLyricsV2 SET SchemaVersion=99 WHERE MusicId=?", editMusic.Id);
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(editCard).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => home.IsInDetailMode && home.DetailEditor is { IsLoading: false, LoadFailed: true });
            editSession = home.DetailEditor!;
            Check(((InfoBar)details.FindName("EditorErrorInfo")).IsOpen && editSession.LoadCommand.CanExecute(null)
                && !editSession.SaveDatabaseCommand.CanExecute(null),
                "initial lyrics-load failure offers a visible retry without enabling an uninitialized save");
            await database.ExecuteAsync("UPDATE MusicLyricsV2 SET SchemaVersion=2 WHERE MusicId=?", editMusic.Id);
            await editSession.LoadCommand.ExecuteAsync(null);
            Check(!editSession.LoadFailed && editSession.Lyrics.CanSave && editSession.Lyrics.OriginalText.Contains("Remote database lyrics"),
                "load retry restores the real persisted lyric version and editable controls");
            page.CollapseDetail();
            await Wait(() => !home.HasDetails);
            await editSession.StopAsync();
            var coverDrainGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editorFactory.CoverGate = coverDrainGate.Task;
            int coverReadsBefore = editorFactory.CoverReads;
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(editCard).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => home.IsInDetailMode && editorFactory.CoverReads > coverReadsBefore);
            var closingCoverEditor = home.DetailEditor!;
            await closingCoverEditor.LoadCommand.ExecuteAsync(null);
            page.CollapseDetail();
            await Wait(() => !home.HasDetails);
            var drainingCover = closingCoverEditor.StopAsync();
            Check(!drainingCover.IsCompleted && !((ProgressRing)details.FindName("ActivityIndicator")).IsActive,
                "editor close stops its hidden activity indicator while waiting for a cover operation that cannot be cancelled");
            coverDrainGate.SetResult();
            await drainingCover;
            editorFactory.CoverGate = Task.CompletedTask;
            Check(closingCoverEditor.CoverPreview is null && home.DetailEditor is null,
                "late cover results cannot reattach an image after navigation has released the editor");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 740));
            await Task.Delay(820);
            var card = Descendants<HomeHeroCard>(page).First();
            Check(((MusicCoverControl)card.FindName("Artwork")).Visibility == Visibility.Visible
                && Grid.GetRow(collage) == 0 && Grid.GetColumn(collage) == 1
                && Grid.GetRowSpan((ContentPresenter)carousel.FindName("PrimaryThumbnail")) == 1
                && primary.ActualWidth < 250 && slider.ActualWidth / slider.ActualHeight is > 1.16 and < 1.3,
                "narrow carousel and collage stay side by side and shrink together without enlarging wrapped tiles");
            await Capture(page, "home-narrow.png");
            var narrowTile = Descendants<HomeCarouselTile>(primary).Single();
            typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [narrowTile]);
            await Wait(() => preview.Visibility == Visibility.Visible && ReferenceEquals(home.PreviewSong, narrowTile.Song));
            await Task.Delay(260);
            var previewPoint = preview.TransformToVisual(contentScroll).TransformPoint(new Windows.Foundation.Point());
            Check(previewPoint.X >= 11 && previewPoint.Y >= 11
                && previewPoint.X + preview.ActualWidth <= contentScroll.ViewportWidth - 11
                && previewPoint.Y + preview.ActualHeight <= contentScroll.ViewportHeight - 11,
                "wider details still fit the visible viewport in a 500-pixel narrow window");
            await Capture(page, "home-narrow-detail.png");
            home.DismissPreview();
            var narrowHost = Descendants<HomeSongItem>((ContentPresenter)carousel.FindName("PrimaryThumbnail")).Single();
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(narrowHost).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => home.IsInDetailMode && !Field<bool>(page, "_detailTransitioning"));
            Check(Grid.GetRow((StackPanel)details.FindName("HeroText")) == 0 && ((Border)details.FindName("CoverHost")).ActualWidth == 112,
                "compact song properties use a square cover and identity ribbon above the editing workspace");
            await Capture(page, "home-song-properties-narrow.png");
            Check(!offThread, "view-model state notifications stay on the real WinUI dispatcher");
            var retained = home.Songs;
            data.WhenInitialized = Task.FromException(new IOException("fixture database failure"));
            statistics.Notify();
            home.RefreshCommand.Execute(null);
            await Wait(() => home.ErrorMessage.Length > 0);
            Check(ReferenceEquals(retained, home.Songs) && home.HasContent, "failed history refresh retains the usable previous snapshot");
            app.Replace([]);
            await Wait(() => home.IsEmpty);
            Check(!home.HasDetails && !app.IsBackBtnEnable && details.Visibility == Visibility.Collapsed,
                "removing the displayed track closes properties even if recommendation refresh has failed");
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
            var exitPlaylistGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.PlaylistGate = exitPlaylistGate.Task;
            var exitMusic = songs.First(song => song.Id != selected.Music.Id);
            var exitPlaylistWrite = home.AddToPlaylistCommand.ExecuteAsync(new HomePlaylistTarget(2, exitMusic.Id));
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
                await Task.Delay(60);
                Check(!stopping.IsCompleted && !exitPlaylistWrite.IsCompleted,
                    "exit waits for in-flight playlist writes as well as recommendation queries");
                exitPlaylistGate.SetResult();
                await stopping;
                Check(await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PlayListMusic WHERE PlayListId = 2 AND MusicId = ?", exitMusic.Id) == 1,
                    "the accepted playlist addition completes before the page dependencies are released");
            }
            finally
            {
                if (blockingConnection.IsInTransaction) blockingConnection.Execute("ROLLBACK");
                exitPlaylistGate.TrySetResult();
            }
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
