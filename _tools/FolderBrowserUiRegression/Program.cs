using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
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
            _ = new RegressionApp();
        });
    }
}
internal sealed partial class RegressionApp : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider _native = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    private readonly IXamlMetadataProvider _local = (IXamlMetadataProvider)Activator.CreateInstance(typeof(Program).Assembly.GetTypes().Single(t => t.FullName!.EndsWith(".XamlMetaDataProvider")))!;
    public IXamlType GetXamlType(Type type) => _local.GetXamlType(type) ?? _native.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _local.GetXamlType(name) ?? _native.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private readonly List<string> _results = [];
    private Window _window = null!;
    private void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        _results.Add("PASS: " + name);
    }
    private static async Task Wait(Func<bool> ready)
    {
        for (int i = 0; i < 200; i++) { if (ready()) return; await Task.Delay(20); }
        throw new TimeoutException("UI did not settle");
    }
    public RegressionApp()
    {
        UnhandledException += (_, args) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "unhandled.txt"), args.Exception.ToString());
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            RequestedTheme = ApplicationTheme.Dark;
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Style/BtnStyle.xaml") });
            var app = new AppViewModel();
            var db = new MusicDatabaseService { Roots = [new() { Path = @"D:\Music", Name = "Music library" }, new() { Path = @"E:\Music", Name = "Archive" }] };
            app.State.Library.Replace([
                new() { FolderPath = @"D:\Music", Title = "A song in the library root" },
                new() { FolderPath = @"D:\Music\Artist\Album", Title = "First track" },
                new() { FolderPath = @"D:\Music\Artist\Album", Title = "Second track" },
                new() { FolderPath = @"E:\Music\Artist\Album", Title = "A different album at another location" }
            ]);
            var services = new ServiceCollection().AddSingleton(app).AddSingleton(db).AddSingleton<MusicBrowseViewModel>()
                .AddSingleton<LibraryProjectionService>().AddSingleton<FolderViewModel>().AddSingleton<AddFolderViewModel>()
                .AddSingleton<ILogger<FolderViewModel>>(NullLogger<FolderViewModel>.Instance).BuildServiceProvider();
            App.Services = services;
            var vm = services.GetRequiredService<FolderViewModel>();
            var projection = services.GetRequiredService<LibraryProjectionService>();
            _window = new Window { Title = "Folder browser regression" };
            var frame = new Frame { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)) };
            _window.Content = frame;
            _window.AppWindow.Resize(new(1320, 900));
            _window.AppWindow.Move(new(50, 30));
            _window.Activate();
            await CheckCompositionCacheAsync();
            frame.Navigate(typeof(FolderBrowsePage));
            await Wait(() => frame.Content is FolderBrowsePage { IsLoaded: true } && !vm.IsLoading && vm.Roots.Count == 2);
            var page = (FolderBrowsePage)frame.Content;
            double scale = page.XamlRoot.RasterizationScale;
            _window.AppWindow.Resize(new((int)(1320 * scale), (int)(900 * scale)));
            await Wait(() => page.ActualWidth >= 1100);
            await Wait(() => !vm.IsBusy);
            Check(vm.SelectedNode is not null && !app.IsBackBtnEnable, "Initial tree selection needs no directory back navigation");
            var root = vm.Roots.Single(n => n.Path == @"D:\Music");
            var tree = (TreeView)page.FindName("FolderTree");
            tree.SelectedItem = root;
            await Wait(() => vm.SelectedNode == root && !vm.IsBusy && app.FolderSongs.Count == 1);
            Check(app.State.Browse.CurrentFolder?.Path == root.Path, "Native tree selection updates the shared folder identity and songs");
            await Wait(() => tree.ContainerFromItem(root) is TreeViewItem);
            ((TreeViewItem)tree.ContainerFromItem(root)).IsExpanded = true;
            var artist = root.Children[0];
            await Wait(() => root.VisibleChildren.Count == 1 && tree.ContainerFromItem(artist) is not null);
            Check(root.IsExpanded, "Native expansion realizes the next folder level lazily");
            SelectNativeRow(tree, artist);
            Check(ReferenceEquals(vm.SelectedNode, artist) && vm.CurrentPath == artist.Path,
                "Native row selection publishes this event's folder rather than the previous SelectedItem");
            await Wait(() => vm.SelectedNode == artist && !vm.IsBusy);
            Check(app.FolderSongs.Count == 0 && vm.IsEmpty && !vm.ShowAddFolder,
                "A parent without direct songs keeps its subfolders in the tree and shows a song empty state");
            ((TreeViewItem)tree.ContainerFromItem(artist)).IsExpanded = true;
            var album = artist.Children[0];
            await Wait(() => tree.ContainerFromItem(album) is not null);
            tree.SelectedItem = album;
            Check(app.FolderSongs.Count == 0 && !vm.HasSongs, "Changing selection hides old playable rows during asynchronous publication");
            await Wait(() => vm.SelectedNode == album && !vm.IsBusy && app.FolderSongs.Count == 2);
            Check(!app.IsBackBtnEnable && tree.SelectedItem == album, "Nested folders remain selected without enabling a back command");
            page.UpdateLayout();
            var treePane = (Grid)page.FindName("TreePane");
            var songsPane = (Grid)page.FindName("SongsPane");
            double songsLeft = songsPane.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).X;
            double treeRight = treePane.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point(treePane.ActualWidth, 0)).X;
            Check(songsLeft > treeRight && songsPane.ActualWidth > treePane.ActualWidth, "Wide layout places the song pane to the right of the folder tree");
            var splitter = (GridSplitter)page.FindName("PaneSplitter");
            var columns = (Grid)page.FindName("BrowserColumns");
            double leftBeforeDrag = treePane.ActualWidth;
            double rightBeforeDrag = songsPane.ActualWidth;
            await DragSplitterAsync(splitter, 80);
            page.UpdateLayout();
            Check(treePane.ActualWidth > leftBeforeDrag + 60 && songsPane.ActualWidth < rightBeforeDrag - 60,
                "GridSplitter drag handling widens the folder pane and shrinks the song pane: " + leftBeforeDrag + " -> " + treePane.ActualWidth);
            Check(columns.ColumnDefinitions[0].Width.Equals(vm.FolderColumnWidth)
                && columns.ColumnDefinitions[2].Width.Equals(vm.SongsColumnWidth),
                "Dragged column widths are published through two-way ViewModel bindings");
            var savedFolderWidth = vm.FolderColumnWidth;
            var savedSongsWidth = vm.SongsColumnWidth;
            await Capture(page, "wide.png");
            _window.AppWindow.Resize(new((int)(760 * scale), (int)(720 * scale)));
            await Wait(() => page.ActualWidth < 900);
            await Task.Delay(200);
            Check(treePane.Visibility == Visibility.Visible && treePane.ActualWidth >= 170,
                "Compact windows keep the tree visible in the left column");
            Check(songsPane.ActualWidth >= 400 && songsPane.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).X > treePane.ActualWidth,
                "Compact windows retain a usable song pane in the right column");
            await DragSplitterAsync(splitter, 171 - treePane.ActualWidth);
            page.UpdateLayout();
            Check(treePane.ActualWidth >= 169 && treePane.ActualWidth <= 172,
                "The folder pane stops at its minimum width in GridSplitter drag handling: " + treePane.ActualWidth);
            await DragSplitterAsync(splitter, columns.ColumnDefinitions[2].ActualWidth - 281);
            page.UpdateLayout();
            Check(columns.ColumnDefinitions[2].ActualWidth >= 279 && columns.ColumnDefinitions[2].ActualWidth <= 282,
                "The song pane minimum width limits GridSplitter drag handling: " + columns.ColumnDefinitions[2].ActualWidth);
            double rightAtLimit = columns.ColumnDefinitions[2].ActualWidth;
            await DragSplitterAsync(splitter, 100);
            page.UpdateLayout();
            Check(columns.ColumnDefinitions[2].ActualWidth >= 280 && Math.Abs(columns.ColumnDefinitions[2].ActualWidth - rightAtLimit) < 2,
                "A drag past the song pane minimum is rejected without collapsing content");
            vm.FolderColumnWidth = savedFolderWidth;
            vm.SongsColumnWidth = savedSongsWidth;
            page.UpdateLayout();
            await Capture(page, "compact.png");
            tree.SelectedItem = root;
            await Wait(() => vm.SelectedNode == root && !vm.IsBusy && app.FolderSongs.Count == 1);
            Check(ReferenceEquals(frame.Content, page), "Selecting an ancestor in the tree stays on the same page");
            frame.Navigate(typeof(Page));
            Check(app.State.Browse.CurrentFolder?.Path == root.Path, "Leaving the page preserves the selected folder");
            frame.Navigate(typeof(FolderBrowsePage));
            await Wait(() => frame.Content is FolderBrowsePage { IsLoaded: true } && !vm.IsBusy);
            Check(vm.SelectedNode?.Path == root.Path && !app.IsBackBtnEnable, "Recreated page restores tree selection without back navigation");
            var restoredColumns = (Grid)((FolderBrowsePage)frame.Content).FindName("BrowserColumns");
            Check(restoredColumns.ColumnDefinitions[0].Width.Equals(savedFolderWidth)
                && restoredColumns.ColumnDefinitions[2].Width.Equals(savedSongsWidth),
                "Recreated pages retain the dragged split ratio for the session");
            app.IsBackBtnEnable = true;
            var previousRoot = vm.SelectedNode;
            projection.RequestRefresh();
            await Wait(() => !ReferenceEquals(vm.SelectedNode, previousRoot) && !vm.IsBusy);
            Check(app.IsBackBtnEnable, "A background folder refresh preserves the song detail presenter back state");
            app.IsBackBtnEnable = false;
            db.Roots.RemoveAt(1);
            app.State.Library.Replace(app.SongsSource.Where(m => !m.FolderPath.StartsWith("E:")).ToList());
            projection.RequestRefresh();
            await Wait(() => vm.Roots.Count == 1 && !vm.IsBusy);
            Check(vm.Roots[0].Path == root.Path, "Library refresh removes a deleted scan root");
            frame.Navigate(typeof(AddFolderPage));
            await Wait(() => frame.Content is AddFolderPage { IsLoaded: true });
            var sourcesPage = (AddFolderPage)frame.Content;
            var addSource = FindChild<DropDownButton>(sourcesPage) ?? throw new Exception("Missing add-source dropdown");
            addSource.ApplyTemplate();
            sourcesPage.UpdateLayout();
            Check(addSource.Template is not null && VisualTreeHelper.GetChildrenCount(addSource) > 0,
                "Production AddFolderPage loads its default DropDownButton template without a missing style resource");
            var sourceMenu = (MenuFlyout)addSource.Flyout;
            Check(sourceMenu.Items.Count == 2 && sourceMenu.Items[0] is MenuFlyoutItem { Command: not null },
                "The source dropdown retains local-folder and WebDAV entries");
            sourceMenu.ShowAt(addSource);
            await Wait(() => sourceMenu.IsOpen);
            Check(sourceMenu.IsOpen, "The source dropdown flyout opens with the global animation style");
            sourceMenu.Hide();
            await CheckSiblingSelectionAsync(frame, app, db, vm, projection);
            await vm.StopAsync();
            var slowDb = new MusicDatabaseService { Pending = new() };
            var slow = new FolderViewModel(new(), app, slowDb, projection, NullLogger<FolderViewModel>.Instance);
            var loading = slow.ActivateAsync();
            await Task.Delay(20);
            var stopping = slow.StopAsync();
            Check(!stopping.IsCompleted, "Shutdown waits for an in-flight SQLite read after cancellation");
            slowDb.Pending.SetResult([new() { Path = @"X:\Late", Name = "Late" }]);
            await stopping;
            Check(slow.Roots.Count == 0, "Late database completion cannot publish after shutdown");
            var failureDb = new MusicDatabaseService { Fail = true };
            var failure = new FolderViewModel(new(), app, failureDb, projection, NullLogger<FolderViewModel>.Instance);
            await failure.ActivateAsync();
            Check(failure.Error.Length > 0 && !failure.IsLoading, "Database failure publishes an actionable error");
            failureDb.Fail = false;
            await failure.RetryCommand.ExecuteAsync(null);
            Check(failure.Error.Length == 0, "Retry recovers from a failed directory read");
            await failure.StopAsync();
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "result.txt"), _results);
        }
        catch (Exception exception) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: " + exception); }
        finally { _window?.Close(); Exit(); }
    }
    private async Task DragSplitterAsync(GridSplitter splitter, double delta)
    {
        // 非交互执行桌面不能可靠命中鼠标目标；直接驱动实际 Toolkit 控件的拖动处理，
        // 检查真实 Grid 列宽、约束和双向绑定，不在测试中重写调整宽度的算法。
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var begin = typeof(GridSplitter).GetMethod("OnDragStarting", flags)!;
        var move = typeof(GridSplitter).GetMethod("OnDragHorizontal", flags)!;
        if (begin.Invoke(splitter, null) is false) throw new Exception("GridSplitter rejected the drag");
        move.Invoke(splitter, [delta]);
        await Task.Yield();
    }

    private async Task CheckSiblingSelectionAsync(Frame frame, AppViewModel app, MusicDatabaseService db,
        FolderViewModel vm, LibraryProjectionService projection)
    {
        string[] names = ["2018 - Vessels", "20200415", "超时空辉夜姬", "Girls Band Cry", "Life Awaits - Butterflies", "Life Awaits - Call", "Life Awaits - Flux"];
        db.Roots = [new() { Path = @"D:\Music", Name = "Music" }];
        app.State.Library.Replace(names.Select(name => new Music { FolderPath = @"D:\Music\" + name, Title = name + " track" }).ToList());
        frame.Navigate(typeof(FolderBrowsePage));
        await Wait(() => frame.Content is FolderBrowsePage { IsLoaded: true } && !vm.IsBusy && vm.Roots.Count == 1);
        var page = (FolderBrowsePage)frame.Content;
        var tree = (TreeView)page.FindName("FolderTree");
        var root = vm.Roots[0];
        await Wait(() => tree.ContainerFromItem(root) is TreeViewItem);
        ((TreeViewItem)tree.ContainerFromItem(root)).IsExpanded = true;
        await Wait(() => root.VisibleChildren.Count == names.Length && root.Children.All(n => tree.ContainerFromItem(n) is not null));
        foreach (int index in new[] { 4, 0, 6, 1, 5, 2, 3 })
        {
            var node = root.Children.Single(n => n.Name == names[index]);
            SelectNativeRow(tree, node);
            await Wait(() => !vm.IsBusy);
            Check(ReferenceEquals(vm.SelectedNode, node) && vm.Title == node.Name && vm.CurrentPath == node.Path
                && app.State.Browse.CurrentFolder?.Path == node.Path
                && app.FolderSongs.Count == 1 && app.FolderSongs[0].Title == names[index] + " track",
                "Native sibling selection keeps the highlighted row, header, path and song aligned: " + names[index]);
        }
        foreach (var node in root.Children) SelectNativeRow(tree, node);
        var last = root.Children[^1];
        await Wait(() => !vm.IsBusy);
        Check(vm.CurrentPath == last.Path && app.FolderSongs[0].Title == last.Name + " track",
            "Rapid native selections publish only the latest folder's songs");
        var selectedLocation = vm.SelectedNode!.Location;
        projection.RequestRefresh();
        await Wait(() => !ReferenceEquals(vm.SelectedNode, last) && !vm.IsBusy);
        await Wait(() => tree.ContainerFromItem(vm.SelectedNode) is TreeViewItem { IsSelected: true });
        Check(vm.SelectedNode!.Location == selectedLocation && vm.CurrentPath == selectedLocation.Path,
            "Tree regeneration restores the same highlighted folder and content");
        await Capture(page, "selection-aligned.png");
    }

    private static void SelectNativeRow(TreeView tree, FolderBrowserNode node)
    {
        var container = tree.ContainerFromItem(node) as TreeViewItem ?? throw new Exception("Folder row is not realized");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(container) ?? new TreeViewItemAutomationPeer(container);
        var provider = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
            ?? throw new Exception("Folder row has no native selection provider");
        provider.Select();
        if (!container.IsSelected) throw new Exception("Native row did not become selected");
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }

    private async Task Capture(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        await Task.Delay(400);
        await WindowCapture.CaptureAsync(_window, name);
    }
}
