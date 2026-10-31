using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

internal static class Program
{
    internal static readonly string Output = Path.Combine(AppContext.BaseDirectory, "artifacts");
    [STAThread]
    private static void Main()
    {
        Directory.CreateDirectory(Output);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new RegressionApp();
        });
    }
}

internal sealed partial class RegressionApp : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider _native = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    private readonly IXamlMetadataProvider _local = (IXamlMetadataProvider)Activator.CreateInstance(typeof(Program).Assembly.GetTypes()
        .Single(t => t.FullName!.EndsWith(".XamlMetaDataProvider", StringComparison.Ordinal)))!;
    public IXamlType GetXamlType(Type type) => _local.GetXamlType(type) ?? _native.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _local.GetXamlType(name) ?? _native.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Style/FloatingCommandStyle.xaml") });
        UnhandledException += (_, error) => File.WriteAllText(Path.Combine(Program.Output, "unhandled.txt"), error.Exception.ToString());
        new TestWindow().ShowInBackground();
    }
}

internal sealed partial class TestWindow : Window
{
    private readonly MainPage _shell = new();
    private readonly AppViewModel _app = new();
    private readonly Grid _root = new() { RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } } };
    private readonly List<string> _checks = [];
    private readonly List<object> _measurements = [];
    private readonly System.Threading.Timer _monitor;
    private readonly IReadOnlyList<Music> _all = Enumerable.Range(1, 1200).Select(id => new Music { Id = id, Title = $"Song {id:D4}" }).ToArray();
    private int _foregroundViolations;
    private int _sequence;
    private nint _hwnd;
    private bool _busy;
    private string _status = "starting";
    private double _savedSongTop;
    private SongListPage? _songPage;
    private FavouritePlayListPage? _favoritePage;
    public TestWindow()
    {
        Title = "Manual track location regression (background)";
        var services = new ServiceCollection();
        services.AddLoggingForFixture();
        services.AddSingleton(_app).AddSingleton(_shell);
        services.AddSingleton<AlbumViewModel>().AddSingleton<ArtistViewModel>().AddSingleton<FolderViewModel>();
        services.AddSingleton<MusicBrowseViewModel>().AddSingleton<SongListViewModel>().AddSingleton<FavouritePlayListViewModel>();
        services.AddSingleton<BassPlayerCommandService>().AddSingleton<SystemMediaControlsService>().AddSingleton<MusicDatabaseService>()
            .AddSingleton<UsbDeviceService>().AddSingleton<PlaybackCoordinator>().AddSingleton<PlaybackCommands>().AddSingleton<CoverPresentationService>()
            .AddSingleton<AddFolderViewModel>();
        services.AddSingleton(provider => new LibraryNavigationService(() => _shell, NullLogger<LibraryNavigationService>.Instance));
        App.Services = services.BuildServiceProvider();
        App.MainWindow = this;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        AddAction(actions, "PrepareSongs", PrepareSongs);
        AddAction(actions, "VerifySongs", VerifySongs);
        AddAction(actions, "PrepareFavorites", PrepareFavorites);
        AddAction(actions, "VerifyFavorites", VerifyFavorites);
        AddAction(actions, "Lifecycle", CheckLifecycle);
        AddAction(actions, "VerifyReentry", VerifyReentry);
        AddAction(actions, "Navigation", CheckNavigation);
        AddAction(actions, "Positions", CheckBrowsePositions);
        AddAction(actions, "Edges", CheckEdges);
        AddAction(actions, "Exit", async () => { await Task.Delay(500); Close(); });
        _root.Children.Add(actions);
        Grid.SetRow(_shell, 1);
        _root.Children.Add(_shell);
        Content = _root;
        _root.Loaded += async (_, _) => await Run("ready", async () =>
        {
            await _app.ListSongs.ReplaceAllAsync(_all);
            await _app.FavoriteSongs.ReplaceAllAsync(_all);
            _app.SongsSource.AddRange(_all);
            PrepareGroupSources();
            await _app.AlbumSongs.ReplaceAllAsync(_all);
            await _app.ArtistSongs.ReplaceAllAsync(_all);
            await _app.FolderSongs.ReplaceAllAsync(_all);
            _shell.NavigateToMusicBrowsePage();
            await Wait(() => _shell.CurrentLibraryPage?.IsLoaded == true);
            await Task.Delay(400);
            if (Environment.GetEnvironmentVariable("FRAME_CACHE_MEMORY") == "1")
            {
                await CheckNavigationCacheMemory();
                await Task.Delay(100);
                Close();
                return;
            }
            _songPage = InnerFrame.Content as SongListPage;
            Check(_songPage is not null, "initial browser defaults to the actual songs page");
            Check(Selector.SelectedItem?.Tag?.ToString() == "song", "selector visually selects initial string tag");
        });
        _monitor = new System.Threading.Timer(_ =>
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint process);
            if (process == Environment.ProcessId) Interlocked.Increment(ref _foregroundViolations);
        }, null, 0, 10);
        Closed += (_, _) => _monitor.Dispose();
    }
    private Frame InnerFrame => (Frame)_shell.CurrentLibraryPage!.FindName("ContentFrame");
    private SelectorBar Selector => (SelectorBar)_shell.CurrentLibraryPage!.FindName("selectPage");
    private static ListView Songs(Page page) => (ListView)page.FindName("MusicListView");
    private static Button Locate(Page page) => (Button)page.FindName("LocateCurrentTrackButton");
    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = TryFind<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        throw new InvalidOperationException("Missing " + typeof(T).Name);
    }
    private static T? TryFind<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (TryFind<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private static async Task Wait(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(20); }
        throw new TimeoutException("UI condition did not settle");
    }
    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks.Add(name);
    }
    private void AddAction(StackPanel panel, string name, Func<Task> action)
    {
        var button = new Button { Content = name };
        AutomationProperties.SetAutomationId(button, name);
        button.Click += async (_, _) => await Run(name, action);
        panel.Children.Add(button);
    }
    private async Task Run(string action, Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        Save(action);
        try { await operation(); _status = "PASS"; }
        catch (Exception error)
        {
            _status = "FAIL: " + error.Message;
            File.WriteAllText(Path.Combine(Program.Output, $"error-{action}.txt"), error.ToString());
        }
        finally { _busy = false; _sequence++; Save(action); }
    }
    private void Save(string action) => File.WriteAllText(Path.Combine(Program.Output, "state.json"), JsonSerializer.Serialize(new
    {
        Action = action, Busy = _busy, Sequence = _sequence, Status = _status, Checks = _checks,
        Measurements = _measurements, ForegroundViolations = _foregroundViolations,
        Hwnd = $"0x{_hwnd:X}"
    }, new JsonSerializerOptions { WriteIndented = true }));
    private void CheckFloatingButton(Page page, string label)
    {
        var button = Locate(page);
        var list = Songs(page);
        var position = button.TransformToVisual(list).TransformPoint(new Point());
        Check(button.ActualWidth == 44 && button.ActualHeight == 44 && button.CornerRadius.TopLeft == 22,
            label + " uses the custom circular floating command style");
        Check(Math.Abs(list.ActualWidth - position.X - button.ActualWidth - 16) < 1.5
            && Math.Abs(list.ActualHeight - position.Y - button.ActualHeight - 16) < 1.5,
            label + " overlays the list lower-right corner with a 16-DIP inset");
        Check(ReferenceEquals(VisualTreeHelper.GetParent(button), VisualTreeHelper.GetParent(list)),
            label + " shares the list grid instead of consuming a toolbar row");
        Check(button.UseSystemFocusVisuals, label + " keeps native keyboard focus visuals");
        Check(button.Background is SolidColorBrush brush && brush.Color.A == 255, label + " has an opaque themed floating surface");
    }
    private async Task PrepareSongs()
    {
        var page = _songPage!;
        CheckFloatingButton(page, "songs");
        var list = Songs(page);
        var scroller = Find<ScrollViewer>(list);
        double before = scroller.VerticalOffset;
        _app.SelectedPlaybackMusic = new Music { Id = 811 };
        page.ViewModel.ReceiveNavigation();
        await App.Services.GetRequiredService<PlaybackCoordinator>().PlayAsync(_app.SelectedPlaybackMusic);
        await Task.Delay(450);
        Check(Math.Abs(before - scroller.VerticalOffset) < 1, "navigation and track start do not scroll songs automatically");
        Check(_songPage!.ViewModel.SelectedMusic is null, "navigation and playback do not select the playing row");
        Check(Locate(page).IsEnabled, "button reacts to current playback change");
        Check(AutomationProperties.GetName(Locate(page)) == "Locate current track", "actual button loads localized automation name");
        Check(ToolTipService.GetToolTip(Locate(page))?.ToString() == "Locate current track", "actual button loads localized tooltip");
    }
    private async Task VerifySongs()
    {
        await Task.Delay(500);
        var list = Songs(_songPage!);

        Check(ReferenceEquals(list.SelectedItem, _app.ListSongs[810]), "selection uses current projection instance by ID");
        CheckCentered(list, _app.ListSongs[810], "songs virtualized target");
        _savedSongTop = ((FrameworkElement)list.ContainerFromIndex(810)).TransformToVisual(list).TransformPoint(new Point()).Y;
        await Capture("songs-centered.png");
    }
    private async Task PrepareFavorites()
    {
        _shell.CurrentLibraryPage!.SelectBarItem("favourite");
        await Wait(() => InnerFrame.Content is FavouritePlayListPage { IsLoaded: true });
        await Task.Delay(400);
        _favoritePage = (FavouritePlayListPage)InnerFrame.Content;
        CheckFloatingButton(_favoritePage, "favorites");
        _app.SelectedPlaybackMusic = new Music { Id = 477 };
        _favoritePage.ViewModel.ReceiveNavigation();
        await Task.Delay(250);
        Check(Find<ScrollViewer>(Songs(_favoritePage)).VerticalOffset < 1, "favorite entry and track change preserve initial scroll");
        Check(Selector.SelectedItem?.Tag?.ToString() == "favourite", "selector reflects favorite tag after navigation");
    }
    private async Task VerifyFavorites()
    {
        await Task.Delay(500);
        var page = _favoritePage!;
        var list = Songs(page);
        Check(ReferenceEquals(list.SelectedItem, _app.FavoriteSongs[476]), "favorite button selects the displayed projection instance");
        CheckCentered(list, _app.FavoriteSongs[476], "favorites virtualized target");
        double offset = Find<ScrollViewer>(list).VerticalOffset;
        var anchor = (FrameworkElement)list.ContainerFromItem(_app.FavoriteSongs[476]);
        double anchorTop = anchor.TransformToVisual(list).TransformPoint(new Point()).Y;
        _app.SelectedPlaybackMusic = new Music { Id = 1000 };
        await Task.Delay(250);
        double afterTop = anchor.TransformToVisual(list).TransformPoint(new Point()).Y;
        _measurements.Add(new { Label = "track change anchor", ErrorDip = Math.Abs(anchorTop - afterTop), BeforeOffset = offset, AfterOffset = Find<ScrollViewer>(list).VerticalOffset });
        Check(Math.Abs(anchorTop - afterTop) < 1.5, $"changing track keeps the visible manual anchor (offset {offset}->{Find<ScrollViewer>(list).VerticalOffset}, anchor {anchorTop}->{afterTop})");
        await _app.FavoriteSongs.ReplaceAllAsync(_all.Where(m => m.Id != 1000));
        await Task.Delay(100);
        Check(!Locate(page).IsEnabled, "filtered-out current track disables the button through real x:Bind");
        var selection = page.ViewModel.SelectedMusic;
        Check(page.ViewModel.LocateCurrentTrack() is null && ReferenceEquals(selection, page.ViewModel.SelectedMusic), "input safely ignores a track absent from displayed projection");
        await _app.FavoriteSongs.ReplaceAllAsync(_all.Where(m => m.Id != 999));
        await Task.Delay(100);
        Check(Locate(page).IsEnabled, "same-count bulk replacement refreshes button availability");
    }
    private async Task CheckLifecycle()
    {
        var previousSong = _songPage;
        for (int i = 0; i < 5; i++)
        {
            _shell.CurrentLibraryPage!.SelectBarItem("song");
            await Task.Delay(120);
            _shell.CurrentLibraryPage.SelectBarItem("favourite");
            await Task.Delay(120);
        }
        await Task.Delay(600);
        _favoritePage = (FavouritePlayListPage)InnerFrame.Content;
        var scrolling = (ListViewCentering)typeof(FavouritePlayListPage).GetField("_centering", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_favoritePage)!;
        var pending = scrolling.LocateAsync(_app.FavoriteSongs[^20]);
        _shell.CurrentLibraryPage!.SelectBarItem("song");
        Check(!await pending, "unloading cancels an in-flight virtualized location");
        await Task.Delay(300);
        _songPage = (SongListPage)InnerFrame.Content;
        Check(!ReferenceEquals(_songPage, previousSong), "disabled navigation caching creates a fresh songs page");
        Check(_songPage.NavigationCacheMode == Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled,
            "new song page explicitly disables navigation caching");
        var returned = Songs(_songPage);
        double top = (returned.ContainerFromIndex(810) as FrameworkElement)?.TransformToVisual(returned).TransformPoint(new Point()).Y ?? double.NaN;
        _measurements.Add(new { Label = "uncached return anchor", ErrorDip = double.IsFinite(top) ? Math.Abs(top - _savedSongTop) : (double?)null });
        File.WriteAllText(Path.Combine(Program.Output, "position-debug.json"), JsonSerializer.Serialize(new {
            SavedTop = _savedSongTop, CurrentTop = double.IsFinite(top) ? top : (double?)null,
            Offset = Find<ScrollViewer>(returned).VerticalOffset, Snapshot = _app.State.Browse.Positions.Songs.Items.Bookmark
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(Math.Abs(top - _savedSongTop) < 1.5, "rebuilt songs page restores the previous visible anchor");
        Check(typeof(SongListViewModel).GetField("LocateRequested", BindingFlags.NonPublic | BindingFlags.Instance) is null
            && typeof(FavouritePlayListViewModel).GetField("LocateRequested", BindingFlags.NonPublic | BindingFlags.Instance) is null,
            "singletons need no view location subscriptions during rebuilt page reentry");
        _app.SelectedPlaybackMusic = new Music { Id = 700 };
    }
    private async Task VerifyReentry()
    {
        await Task.Delay(400);
        Check(ReferenceEquals(Songs(_songPage!).SelectedItem, _app.ListSongs[699]), "manual button still selects after rapid uncached page reentry");
        CheckCentered(Songs(_songPage!), _app.ListSongs[699], "uncached reentry manual target");
    }
    private async Task CheckNavigation()
    {
        var browser = _shell.CurrentLibraryPage!;
        var vm = browser.ViewModel;
        foreach (var (tag, type) in new[] { ("album", typeof(AlbumPage)), ("artist", typeof(ArtistPage)), ("folder", typeof(FolderBrowsePage)), ("favourite", typeof(FavouritePlayListPage)), ("song", typeof(SongListPage)) })
        {
            browser.SelectBarItem(tag);
            await Task.Delay(100);
            Check(InnerFrame.Content.GetType() == type, "tag navigation maps " + tag);
            Check(Selector.SelectedItem?.Tag?.ToString() == tag, "selector highlights " + tag);
            Check(!InnerFrame.IsNavigationStackEnabled, "native Frame keeps the configured stack behavior for " + tag);
        }
        _shell.ShellFrame.Navigate(typeof(EmptyPage));
        await Task.Delay(150);
        vm.SelectBarAlbum("Album");
        await Task.Delay(150);
        Check(!ReferenceEquals(browser, _shell.CurrentLibraryPage), "cross-link recreates the uncached outer browser");
        Check(_shell.CurrentLibraryPage!.NavigationCacheMode == Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled,
            "outer browser explicitly disables navigation caching");
        Check(InnerFrame.Content is AlbumPage && vm.SelectedPageTag == "album", "album cross-link resolves shell without VM page field");
        vm.SelectBarArtist("Artist");
        await Task.Delay(150);
        Check(InnerFrame.Content is ArtistPage && vm.SelectedPageTag == "artist", "artist cross-link resolves the active browser");
        var replacement = new MusicBrowsePage();
        _shell.ShellFrame.Content = replacement;
        replacement.SelectBarItem("favourite");
        await Wait(() => replacement.IsLoaded);
        await Task.Delay(500);
        Check(InnerFrame.Content is FavouritePlayListPage, "cross-link before initial Loaded restores the requested child page");
        Check(Selector.SelectedItem?.Tag?.ToString() == "favourite", "cross-link before initial Loaded preserves the highlighted selector item");
        _shell.ShellFrame.Content = browser;
        await Task.Delay(250);
        Check(typeof(MusicBrowseViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .All(f => !typeof(DependencyObject).IsAssignableFrom(f.FieldType)), "browser singleton has no page or SelectorBarItem fields");
        foreach (Type type in new[] { typeof(SongListViewModel), typeof(FavouritePlayListViewModel) })
            Check(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).All(f => !typeof(Page).IsAssignableFrom(f.FieldType)), type.Name + " has no retained page field");
    }
    private async Task CheckEdges()
    {
        var browser = _shell.CurrentLibraryPage!;
        browser.SelectBarItem("song");
        await Task.Delay(250);
        _songPage = (SongListPage)InnerFrame.Content;
        var list = Songs(_songPage);
        var centering = new ListViewCentering(list);
        Check(!await centering.LocateAsync(null), "null request is ignored");
        Check(!await centering.LocateAsync(new Music { Id = -1 }), "absent item request is ignored");
        Check(CurrentTrackLookup.Find(_app.ListSongs, null) is null, "null playback has no target");
        Check(ReferenceEquals(CurrentTrackLookup.Find(_app.ListSongs, new Music { Id = 620 }), _app.ListSongs[619]), "lookup resolves an equal ID to displayed instance");
        var wrappers = new List<PlayListMusicItem> { new() { Music = _all[2] } };
        Check(ReferenceEquals(CurrentTrackLookup.Find(wrappers, new Music { Id = 3 }), wrappers[0]), "playlist lookup returns the displayed wrapper");
        Check(CurrentTrackLookup.Find(wrappers, new Music { Id = 4 }) is null, "playlist lookup rejects filtered-out playback");
        var queueVm = new WinUIMusicPlayer.ViewModel.Pages.MainViewModel(_app,
            App.Services.GetRequiredService<BassPlayerCommandService>(), browser.ViewModel,
            new WinUIMusicPlayer.DesktopLyrics.DesktopLyricsViewModel());
        await _app.CurrentPlayingList.ReplaceAllAsync([_all[100], _all[800]]);
        _app.SelectedPlaybackMusic = new Music { Id = 801 };
        Check(queueVm.LocateCurrentTrack() == 1, "shared queue ViewModel resolves the displayed current queue instance");
        Check(queueVm.CanLocateCurrentTrack(_app.SelectedPlaybackMusic, 2, _app.Queue.Version), "queue button is available for a matching track");
        _app.SelectedPlaybackMusic = new Music { Id = 3000 };
        Check(queueVm.LocateCurrentTrack() < 0 && !queueVm.CanLocateCurrentTrack(_app.SelectedPlaybackMusic, 2, _app.Queue.Version), "queue ignores playback absent from current queue");
        var repeated = new Music { Id = 0, Title = "Repeated external item", Path = "C:/fixture/one.flac" };
        var otherExternal = new Music { Id = 0, Title = "Other external item", Path = "C:/fixture/two.flac" };
        var queueItems = _all.ToArray();
        queueItems[20] = repeated;
        queueItems[950] = repeated;
        await _app.CurrentPlayingList.ReplaceAllAsync(queueItems);
        _app.SelectedPlaybackMusic = repeated;
        _app.Queue.SelectEntry(_app.Queue.EntryIdAt(950), repeated);
        Check(queueVm.LocateCurrentTrack() == 950, "queue locator preserves selected repeated entry instead of first equal Music object");
        _app.PendingEntryId = _app.Queue.EntryIdAt(20);
        Check(queueVm.LocateCurrentTrack() == 20, "queue locator follows the pending entry cursor");
        _app.PendingEntryId = 0;
        Check(CurrentTrackLookup.Find(new List<Music> { repeated, otherExternal }, new Music { Id = 0, Path = "C:/fixture/two.flac" }) == otherExternal,
            "external Id=0 lookup compares file paths");
        Check(CurrentTrackLookup.Find(new List<Music> { repeated }, new Music { Id = 0 }) is null, "unidentified external playback cannot match arbitrary Id=0 rows");
        var queueList = new ListView { Height = 360, ItemsSource = _app.CurrentPlayingList, DisplayMemberPath = "Title" };
        _root.Children.Add(queueList);
        Grid.SetRow(queueList, 1);
        await Wait(() => queueList.IsLoaded && queueList.ActualHeight > 0);
        await Task.Delay(100);
        var queueCentering = new ListViewCentering(queueList);
        Check(await queueCentering.LocateIndexAsync(queueVm.LocateCurrentTrack()), "repeated object at a virtualized queue index can be located");
        var queueRow = (FrameworkElement)queueList.ContainerFromIndex(950);
        var queueScroller = Find<ScrollViewer>(queueList);
        double duplicateError = Math.Abs(queueRow.TransformToVisual(queueScroller).TransformPoint(new Point()).Y + queueRow.ActualHeight / 2 - queueScroller.ViewportHeight / 2);
        Check(duplicateError < 1.5, "second occurrence of the same queue object is centered");
        _measurements.Add(new { Label = "duplicate queue entry 950", ErrorDip = duplicateError });
        _root.Children.Remove(queueList);
        foreach (int index in new[] { 300, 999, 50, 700, 1190, 101 })
        {
            Check(await centering.LocateAsync(_app.ListSongs[index]), "location converges for index " + index);
            CheckCentered(list, _app.ListSongs[index], "variable-height row " + index);
        }
        Check(await centering.LocateAsync(_app.ListSongs[0]), "first item request converges");
        Check(Find<ScrollViewer>(list).VerticalOffset < 1, "first item clamps to start");
        Check(await centering.LocateAsync(_app.ListSongs[^1]), "last item request converges");
        var scroller = Find<ScrollViewer>(list);
        Check(Math.Abs(scroller.VerticalOffset - scroller.ScrollableHeight) < 1, "last item clamps to end");
        var cancelledByReset = centering.LocateAsync(_app.ListSongs[500]);
        await _app.ListSongs.ReplaceAllAsync(_all);
        Check(!await cancelledByReset, "collection reset cancels pending location instead of centering a stale row");
        var old = centering.LocateAsync(_app.ListSongs[500]);
        var latest = centering.LocateAsync(_app.ListSongs[900]);
        Check(!await old, "new request cancels prior virtualized operation");
        Check(await latest, "latest rapid request completes");
        CheckCentered(list, _app.ListSongs[900], "rapid request final target");
        _app.SelectedPlaybackMusic = null;
        await Task.Delay(50);
        Check(!Locate(_songPage).IsEnabled, "clearing playback disables the actual button");
        await Capture("songs-final.png");
        Check(_foregroundViolations == 0, "all fixture actions stayed in the background");
    }
    private void CheckCentered(ListView list, object item, string label)
    {
        var scroller = Find<ScrollViewer>(list);
        Check(list.ContainerFromItem(item) is FrameworkElement, label + " container realized");
        var row = (FrameworkElement)list.ContainerFromItem(item);
        double center = row.TransformToVisual(scroller).TransformPoint(new Point()).Y + row.ActualHeight / 2;
        double error = Math.Abs(center - scroller.ViewportHeight / 2);
        _measurements.Add(new { Label = label, ErrorDip = error, Offset = scroller.VerticalOffset, Viewport = scroller.ViewportHeight, RowHeight = row.ActualHeight });
        Check(error < 1.5, label + " centered within 1.5 DIP");
    }
    private async Task Capture(string name)
    {
        await WindowCapture.CaptureAsync(this, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(Program.Output, name), true);
    }
    public void ShowInBackground()
    {
        nint hwnd = _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Native.SetWindowLongPtr(hwnd, -20, (nint)((Native.GetWindowLongPtr(hwnd, -20).ToInt64() | 0x08000000L | 0x80L) & ~0x40000L));
        AppWindow.IsShownInSwitchers = false;
        AppWindow.MoveAndResize(new RectInt32(-20000, -20000, 1060, 780));
        AppWindow.Show(false);
    }
}
internal static class Native
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
internal static class FixtureServices
{
    public static void AddLoggingForFixture(this IServiceCollection services) => services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
}
