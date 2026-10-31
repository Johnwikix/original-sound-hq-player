using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using SQLite;
using Windows.Graphics;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Recommendations;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

internal sealed partial class RegressionApplication
{
    private async Task StartPolishProbeAsync()
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Style/BtnStyle.xaml") });
        string path = Path.Combine(AppContext.BaseDirectory, "polish-fixture.db");
        if (File.Exists(path)) File.Delete(path);
        var database = new SQLiteAsyncConnection(path);
        await database.CreateTableAsync<Music>();
        await database.CreateTableAsync<PlayListMusic>();
        await database.CreateTableAsync<MusicLyrics>();
        await database.CreateTableAsync<PendingMetadataWrite>();
        await database.ExecuteAsync("CREATE TABLE PlaybackHistory (MusicId INTEGER, StartedAt INTEGER, TotalDurationMs REAL, DurationPlayedMs REAL)");
        var app = new AppViewModel();
        var data = new MusicDatabaseService(database);
        await data.Lyrics.InitializeAsync(path);
        var stats = new PlaybackStatsService();
        var editors = new FixtureEditorFactory(data, app);
        var home = new HomeViewModel(app, data, stats, new HomeRecommendationService(), new PlaybackCoordinator(app, stats),
            new AddFolderViewModel(), new WebDavLibraryService(), NullLogger<HomeViewModel>.Instance, editors, editors.Sessions);
        var services = new ServiceCollection().AddSingleton(home).AddSingleton(app).AddSingleton(data)
            .AddSingleton<EditorSessions>(editors.Sessions).AddSingleton<IMusicDetailsEditorFactory>(editors)
            .AddSingleton<LibraryNavigationService>().BuildServiceProvider();
        App.Services = services;
        var songs = Enumerable.Range(1, 40).Select(id => new Music { Id = id, Title = "Probe song " + id,
            Album = "Album " + id, Author = "Artist " + id, ImageHash = "polish-" + id,
            Path = Path.Combine(AppContext.BaseDirectory, "probe-" + id + ".flac") }).ToArray();
        foreach (var song in songs) { WinUIMusicPlayer.Utils.ToolUtils.PrepareArtwork(song.Id); await database.InsertAsync(song); }
        app.Replace(songs.ToList());
        var window = new HomePolishWindow(home, songs, editors);
        App.MainWindow = window;
        window.Closed += async (_, _) => { await home.StopAsync(); services.Dispose(); await database.CloseAsync(); Exit(); };
        window.ShowBackground();
    }
}

internal sealed partial class HomePolishWindow : Window
{
    private readonly HomeViewModel _home;
    private readonly FixtureEditorFactory _editors;
    private readonly Music[] _songs;
    private readonly Grid _root = new();
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly string _output = Environment.GetEnvironmentVariable("HOME_POLISH_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "polish-artifacts");
    private readonly List<string> _checks = [];
    private readonly List<object> _samples = [];
    private readonly List<WeakReference<HomePage>> _pages = [];
    private readonly ConditionalWeakTable<HomePage, PageSentinel> _pageSentinels = new();
    private int _createdPages;
    private int _unloadedPages;
    private static int _finalizedPages;
    private sealed class PageSentinel { ~PageSentinel() => Interlocked.Increment(ref _finalizedPages); }
    private readonly System.Threading.Timer _foreground;
    private HomePage? _page;
    private bool _busy;
    private int _sequence;
    private int _violations;
    private int _failures;
    private nint _hwnd;
    private HomeSongItem? _previewSource;
    private bool _handoffSourceAlive;
    private bool _handoffNativeCompleted;
    private Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation? _watchedHandoff;
    private string _status = "starting";
    public HomePolishWindow(HomeViewModel home, Music[] songs, FixtureEditorFactory editors)
    {
        _home = home; _songs = songs; _editors = editors;
        _home.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(HomeViewModel.IsInDetailMode) && _home.IsInDetailMode)
                DispatcherQueue.TryEnqueue(() =>
                {
                    _handoffNativeCompleted = false;
                    _handoffSourceAlive = _home.PreviewSong is not null;
                    _watchedHandoff = (Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation?)typeof(HomePage)
                        .GetField("_detailAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_page);
                    if (_watchedHandoff is not null) _watchedHandoff.Completed += HandoffCompleted;
                });
        };
        Directory.CreateDirectory(_output);
        Title = "Home polish and memory verification (background)";
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var buttons = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 92, ItemHeight = 30, MaximumRowsOrColumns = 11 };
        Add(buttons, "ReproCarousel", CheckCarousel);
        Add(buttons, "RoundTrip", CheckCoverRoundTrips);
        Add(buttons, "Preview", ShowPreview);
        Add(buttons, "OpenFromPreview", OpenFromPreview);
        Add(buttons, "VerifyDetails", CheckDetails);
        Add(buttons, "Secondary", CheckSecondaryLifetimes);
        Add(buttons, "Universal", CheckUniversalDetails);
        Add(buttons, "DetailsTiming", CheckDetailsTiming);
        Add(buttons, "CarouselEdges", CheckCarouselEdges);
        Add(buttons, "CoverCancel", CheckCoverCancellation);
        Add(buttons, "HoverExit", CheckHoverExit);
        Add(buttons, "HoverFrames", CheckHoverFrames);
        Add(buttons, "HoverWheel", CheckHoverWheel);
        Add(buttons, "CoverCycles", CheckCoverCycles);
        Add(buttons, "Stress", Stress);
        Add(buttons, "Collect", Collect);
        Add(buttons, "Exit", async () => { await Task.Delay(500); Close(); });
        _root.Children.Add(buttons);
        Grid.SetRow(_host, 1); _root.Children.Add(_host);
        Content = _root;
        _root.Loaded += async (_, _) => await Run("ready", async () =>
        {
            _page = CreatePage(); _host.Content = _page;
            await Wait(() => _page.IsLoaded && _home.HasContent && !_home.IsLoading);
            ((FeaturedCarousel)_page.FindName("Featured")).AutoShuffle = false;
            await Task.Delay(800);
            Sample("warm");
            if (Environment.GetEnvironmentVariable("HOME_POLISH_AUTOMEMORY") == "1")
            {
                for (int i = 0; i < 3; i++) { await Stress(); await Collect(); }
                Save("auto-memory");
                await Task.Delay(100);
                Close();
            }
        });
        _foreground = new System.Threading.Timer(_ =>
        {
            PolishNative.GetWindowThreadProcessId(PolishNative.GetForegroundWindow(), out uint pid);
            if (pid == Environment.ProcessId) Interlocked.Increment(ref _violations);
        }, null, 0, 10);
        Closed += (_, _) => _foreground.Dispose();
    }
    private HomePage CreatePage()
    {
        var page = new HomePage { RequestedTheme = ElementTheme.Light, Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243)) };
        _createdPages++;
        page.Unloaded += (_, _) => _unloadedPages++;
        if (Environment.GetEnvironmentVariable("HOME_POLISH_RETAIN_TICKS") == "1")
            page.Unloaded += (_, _) =>
            {
                var timer = (Microsoft.UI.Dispatching.DispatcherQueueTimer)typeof(HomePage).GetField("_openPreviewTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
                var handler = (Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>)Delegate.CreateDelegate(
                    typeof(Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>), page,
                    typeof(HomePage).GetMethod("OpenPreview_Tick", BindingFlags.Instance | BindingFlags.NonPublic)!);
                timer.Tick += handler;
            };
        _pageSentinels.Add(page, new PageSentinel());
        if (Environment.GetEnvironmentVariable("HOME_POLISH_NO_WEAK") != "1") _pages.Add(new(page));
        return page;
    }
    private void HandoffCompleted(Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation sender, object args)
    {
        sender.Completed -= HandoffCompleted;
        _watchedHandoff = null;
        _handoffNativeCompleted = true;
    }
    private void Add(Panel panel, string name, Func<Task> action)
    {
        var button = new Button { Content = name, FontSize = 11, Padding = new Thickness(6, 4, 6, 4) }; AutomationProperties.SetAutomationId(button, name);
        button.Click += async (_, _) => await Run(name, action); panel.Children.Add(button);
    }
    private async Task Run(string action, Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; HomePolishEvents.Log.Phase(action + "-start"); Save(action);
        try { await operation(); _status = "PASS"; }
        catch (Exception error) { _failures++; _status = "FAIL: " + error.Message; File.WriteAllText(Path.Combine(_output, "error-" + action + ".txt"), error.ToString()); }
        finally { HomePolishEvents.Log.Phase(action + "-end"); _busy = false; _sequence++; Save(action); }
    }
    private void Save(string action) => File.WriteAllText(Path.Combine(_output, "state.json"), JsonSerializer.Serialize(new {
        Action = action, Busy = _busy, Sequence = _sequence, Status = _status, Checks = _checks, Samples = _samples,
        Failures = _failures, ForegroundViolations = _violations, Hwnd = $"0x{_hwnd:X}", Pid = Environment.ProcessId
    }, new JsonSerializerOptions { WriteIndented = true }));
    private void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks.Add(message); }
    private static async Task Wait(Func<bool> condition)
    {
        for (int i = 0; i < 250; i++) { if (condition()) return; await Task.Delay(20); }
        throw new TimeoutException("UI did not settle");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private async Task CheckCarousel()
    {
        var failures = new List<string>();
        void Check(bool condition, string name) { if (condition) _checks.Add(name); else failures.Add(name); }
        var carousel = (FeaturedCarousel)_page!.FindName("Featured"); carousel.AutoShuffle = false;
        var hero = (HomeHeroCard)_page.FindName("HomeHero");
        var artwork = (MusicCoverControl)hero.FindName("Artwork");
        artwork.Music = _songs[0]; await Wait(() => ((Image)artwork.FindName("CoverImage")).Source is not null);
        await Task.Delay(750);
        var missing = new Music { Id = 990001, Title = "Missing artwork", Path = "fixture-no-cover.flac" };
        artwork.Music = missing;
        await Wait(() => ((Image)artwork.FindName("CoverImage")).Source is null && WinUIMusicPlayer.Utils.ToolUtils.RawActive == 0);
        Check(ElementCompositionPreview.GetElementVisual((Grid)artwork.FindName("CurrentLayer")).TryGetAnimationController("Offset") is not null,
            "missing artwork participates in the same slide animation");
        Check(VisualTreeHelper.GetParent((FontIcon)artwork.FindName("CoverPlaceholder")) == artwork.FindName("CurrentLayer"),
            "placeholder belongs to the animated current presentation layer");
        await Task.Delay(750);
        artwork.Music = _songs[1]; await Wait(() => ((Image)artwork.FindName("CoverImage")).Source is not null);
        Check(ElementCompositionPreview.GetElementVisual((Grid)artwork.FindName("CurrentLayer")).TryGetAnimationController("Offset") is not null,
            "artwork following a missing cover still slides into view");
        await Task.Delay(800);
        Check(((Image)artwork.FindName("PreviousImage")).Source is null, "completed image animation releases the previous owned source");
        foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            _page.RequestedTheme = theme; artwork.Music = new Music { Id = 990002 + (int)theme, Title = "No cover", Path = "no-cover-" + theme };
            await Wait(() => ((Image)artwork.FindName("CoverImage")).Source is null && WinUIMusicPlayer.Utils.ToolUtils.RawActive == 0);
            var background = ((Grid)artwork.FindName("CurrentLayer")).Background as SolidColorBrush;
            Check(background is { Opacity: 1 } && background.Color.A == 255, theme + " no-cover presentation uses an opaque theme brush");
            await Task.Delay(800);
            await WindowCapture.CaptureAsync(this, "home-polish-placeholder-" + theme + ".png");
        }
        _page.RequestedTheme = ElementTheme.Light;
        await Task.Delay(100);
        foreach (var cover in Descendants<MusicCoverControl>((RecommendationShelf)_page.FindName("SongsShelf")))
            Check(ElementCompositionPreview.GetElementVisual((FrameworkElement?)cover.FindName("CoverPresentation") ?? cover).Clip is Microsoft.UI.Composition.CompositionGeometricClip,
                "recommendation artwork has an explicit rounded composition clip");
        await WindowCapture.CaptureAsync(this, "home-polish-carousel.png");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
    }
    private async Task ShowPreview()
    {
        if (_home.HasDetails) { _page!.CollapseDetail(); await Task.Delay(800); }
        var scroll = (ScrollViewer)_page!.FindName("ContentScroll"); scroll.ChangeView(null, 550, null, true);
        await Task.Delay(150);
        var item = Descendants<HomeSongItem>((RecommendationShelf)_page.FindName("SongsShelf")).First();
        _previewSource = item;
        typeof(HomePage).GetMethod("SchedulePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_page, [item]);
        await Wait(() => _home.PreviewSong is not null);
        await Task.Delay(350);
        Assert(!Descendants<TextBlock>((HomeSongPreview)_page.FindName("SongPreview")).Any(t => t.Text == _home.PreviewSong!.Reason),
            "preview omits the fourth reason row");
        await WindowCapture.CaptureAsync(this, "home-polish-preview.png");
    }
    private async Task OpenFromPreview()
    {
        var preview = (HomeSongPreview)_page!.FindName("SongPreview");
        var artwork = Descendants<MusicCoverControl>(preview).First();
        typeof(HomePage).GetMethod("OpenSongDetails", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_page, [_previewSource, _home.PreviewSong, artwork, false]);
        Assert(_home.PreviewSong is not null && preview.Visibility == Visibility.Visible,
            "preview source remains alive while connected handoff is queued");
        await Task.Delay(80);
        await WindowCapture.CaptureAsync(this, "home-polish-handoff.png");
    }
    private async Task CheckDetails()
    {
        await Wait(() => _home.IsInDetailMode);
        await Task.Delay(900);
        Assert(_home.PreviewSong is null && ((HomeSongPreview)_page!.FindName("SongPreview")).Visibility == Visibility.Collapsed,
            "preview is cleared after the connected handoff completes");
        Assert(_handoffSourceAlive, "preview source survives detail state publication before TryStart");
        Assert(_handoffNativeCompleted, "preview handoff receives real native ConnectedAnimation.Completed rather than an immediate fallback");
        Assert(_home.DetailEditor is not null, "detail editor remains active after preview handoff");
        await WindowCapture.CaptureAsync(this, "home-polish-properties.png");
        _page.CollapseDetail(); await Task.Delay(800);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task Stress()
    {
        HomePolishEvents.Log.Phase("natural-stress");
        _previewSource = null;
        _host.Content = null; _page = null; await Task.Delay(100);
        for (int i = 0; i < 10; i++)
        {
            _host.Content = CreatePage();
            await Wait(() => _host.Content is HomePage { IsLoaded: true });
            await Task.Delay(120);
            if (Environment.GetEnvironmentVariable("HOME_POLISH_DETACH") == "1")
                ClearViewSources((HomePage)_host.Content);
            _host.Content = null;
            await Task.Delay(70);
        }
        await Task.Delay(800); Sample("stress-" + _pages.Count);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearViewSources(HomePage page)
    {
        ((FeaturedCarousel)page.FindName("Featured")).ItemsSource = null;
        ((FeaturedCarousel)page.FindName("Featured")).ThumbnailItemsSource = null;
        ((HomeHeroCard)page.FindName("HomeHero")).Theme = null;
        foreach (string name in new[] { "SongsShelf", "FamiliarShelf", "DiscoveryShelf" })
            ((RecommendationShelf)page.FindName(name)).ItemsSource = null;
    }
    private async Task Collect()
    {
        // Diagnostic button only. Normal navigation and Stress never induce a collection.
        HomePolishEvents.Log.Phase("diagnostic-gc");
        Sample("before-diagnostic-gc");
        for (int i = 0; i < 3; i++)
        {
            if (Environment.GetEnvironmentVariable("HOME_POLISH_UI_GC") == "1")
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
            }
            else await Task.Run(() => { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); });
            await Task.Delay(200);
        }
        Sample("after-diagnostic-gc");
        if (_host.Content is null && _page is null && _pages.Count > 0)
        {
            int alive = _pages.Count(page => page.TryGetTarget(out _));
            bool control = Environment.GetEnvironmentVariable("HOME_POLISH_RETAIN_TICKS") == "1";
            // UIA has inspected the first page; keep that observer out of the lifetime assertion.
            // The separate no-UIA Run-Memory process requires all pages, including the first, to die.
            bool automated = Environment.GetEnvironmentVariable("HOME_POLISH_AUTOMEMORY") == "1";
            int unobservedAlive = _pages.Skip(automated ? 0 : 1).Count(page => page.TryGetTarget(out _));
            Assert(control ? alive == _createdPages : unobservedAlive == 0,
                control ? "restoring only a native Tick callback reproduces retained pages" : "all unloaded pages without UIA observers are reclaimed after owned timer cleanup");
        }
        Assert(_retiredCovers.All(cover => !cover.TryGetTarget(out _)), "reloaded and detached artwork controls are reclaimed");
        Assert(_universalPages.All(page => !page.TryGetTarget(out _)), "all retired universal detail pages are reclaimed");
        Assert(_violations == 0, "all actions stayed outside the foreground");
    }
    private void Sample(string phase)
    {
        var gpu = HomePolish.Diagnostics.GpuCounters.Read();
        using var process = Process.GetCurrentProcess(); process.Refresh(); var gc = GC.GetGCMemoryInfo();
        _samples.Add(new { Phase = phase, ManagedBytes = GC.GetTotalMemory(false), HeapBytes = gc.HeapSizeBytes,
            GcCommitted = gc.TotalCommittedBytes, AllocatedBytes = GC.GetTotalAllocatedBytes(), PrivateBytes = process.PrivateMemorySize64,
            Gpu = gpu, Dxgi = HomePolish.Diagnostics.VideoMemory.Read(), WorkingSet = process.WorkingSet64, Handles = process.HandleCount, Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2),
            GcPausePercent = gc.PauseTimePercentage,
            UnloadedPages = _unloadedPages,
            HomeSubscriptions = ((MulticastDelegate?)typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_home))?.GetInvocationList().GroupBy(d => d.Method.DeclaringType?.Name).ToDictionary(g => g.Key ?? "unknown", g => g.Count()),
            CreatedPages = _createdPages, AlivePages = _pages.Count(p => p.TryGetTarget(out _)),
            AlivePageIndices = _pages.Select((page, index) => (page, index)).Where(pair => pair.page.TryGetTarget(out _)).Select(pair => pair.index).ToArray(), FinalizedPages = _finalizedPages,
            GcOnUiThread = Environment.GetEnvironmentVariable("HOME_POLISH_UI_GC") == "1", NativeAwareWeakReferences = Environment.GetEnvironmentVariable("HOME_POLISH_NO_WEAK") != "1" });
    }
    public void ShowBackground() { _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this); PolishNative.ShowWithoutActivation(this); }
}

internal static class PolishNative
{
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    internal static void ShowWithoutActivation(Window window)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowLongPtr(hwnd, -20, (nint)((GetWindowLongPtr(hwnd, -20).ToInt64() | 0x08000000L | 0x80L) & ~0x40000L));
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, 1120, 900));
        window.AppWindow.Show(false);
    }
}
