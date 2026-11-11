using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using VirtualKey = Windows.System.VirtualKey;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Helper.Animations;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
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
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor,
        "FolderBrowserUiRegression.FolderBrowserUiRegression_XamlTypeInfo.XamlMetaDataProvider", "FolderBrowserUiRegression")]
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
            var tree = (ListView)page.FindName("FolderTree");
            tree.SelectedItem = root;
            await Wait(() => vm.SelectedNode == root && !vm.IsBusy && app.FolderSongs.Count == 1);
            Check(app.State.Browse.CurrentFolder?.Path == root.Path, "Native tree selection updates the shared folder identity and songs");
            await Wait(() => tree.ContainerFromItem(root) is ListViewItem);
            vm.ExpandFolder(root);
            var artist = root.Children[0];
            await Wait(() => vm.VisibleNodes.Contains(artist) && tree.ContainerFromItem(artist) is not null);
            Check(root.IsExpanded, "Native expansion realizes the next folder level lazily");
            SelectNativeRow(tree, artist);
            Check(ReferenceEquals(vm.SelectedNode, artist) && vm.CurrentPath == artist.Path,
                "Native row selection publishes this event's folder rather than the previous SelectedItem");
            await Wait(() => vm.SelectedNode == artist && !vm.IsBusy);
            Check(app.FolderSongs.Count == 0 && vm.IsEmpty && !vm.ShowAddFolder,
                "A parent without direct songs keeps its subfolders in the tree and shows a song empty state");
            vm.ExpandFolder(artist);
            var album = artist.Children[0];
            await Wait(() => tree.ContainerFromItem(album) is not null);
            tree.SelectedItem = album;
            Check(app.FolderSongs.Count == 0 && !vm.HasSongs, "Changing selection hides old playable rows during asynchronous publication");
            await Wait(() => vm.SelectedNode == album && !vm.IsBusy && app.FolderSongs.Count == 2);
            Check(!app.IsBackBtnEnable && tree.SelectedItem == album, "Nested folders remain selected without enabling a back command");
            await CheckKeyboardNavigationAsync(tree, vm, artist, album);
            await CheckTreeMotionAsync(page, tree, vm, root, artist, album);
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
            await CheckDeepTreeRenderAsync(frame, app, db, vm, projection);
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
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: " + exception);
        }
        finally { _window?.Close(); Exit(); }
    }
    private static bool ChevronStateBackgroundsAreTransparent(ToggleButton button)
    {
        button.ApplyTemplate();
        var presenter = button.FindDescendant<ContentPresenter>() ?? throw new Exception("Chevron content presenter is missing");
        try
        {
            foreach (string state in new[] { "Normal", "PointerOver", "Pressed", "Checked", "CheckedPointerOver", "CheckedPressed" })
            {
                if (!VisualStateManager.GoToState(button, state, false)) return false;
                button.UpdateLayout();
                if (presenter.Background is not SolidColorBrush brush || brush.Color.A != 0) return false;
            }
            return true;
        }
        finally
        {
            VisualStateManager.GoToState(button, button.IsChecked == true ? "Checked" : "Normal", false);
        }
    }

    private async Task CheckTreeMotionAsync(FolderBrowsePage page, ListView tree, FolderViewModel vm,
        FolderBrowserNode root, FolderBrowserNode artist, FolderBrowserNode album)
    {
        Check(tree.ItemContainerTransitions.Count == 1
            && tree.ItemContainerTransitions[0] is RepositionThemeTransition { IsStaggeringEnabled: true },
            "Reposition keeps the original native transition, duration and stagger");
        var motion = ReadField<object>(page, "_treeMotion");
        await Wait(() => !ReadField<bool>(motion, "_animating") && !ReadField<bool>(motion, "_layoutPending"));
        FolderBrowserNode[] expanded = vm.VisibleNodes.ToArray();
        int rootIndex = vm.VisibleNodes.IndexOf(root);
        Check(expanded.Length == 4 && rootIndex >= 0 && rootIndex + 2 < expanded.Length
            && expanded[rootIndex + 1] == artist && expanded[rootIndex + 2] == album,
            "The flat projection contains the expanded branch in preorder");

        var rootItem = (ListViewItem)tree.ContainerFromItem(root)!;
        var expander = rootItem.FindDescendant<ToggleButton>()!;
        Check(AutomationProperties.GetName(expander) == root.Name && expander.IsChecked == true
            && ReferenceEquals(expander.Command, FolderCommands.ToggleFolderCommand),
            "The row expander exposes its accessible name, expanded state and model command");
        Check(FluentAnimation.GetPointerOver(expander) is null && FluentAnimation.GetPressed(expander) is null,
            "The chevron opts out of implicit pointer-over and pressed composition animations");
        Check(ChevronStateBackgroundsAreTransparent(expander),
            "Collapsed and expanded chevrons keep transparent PointerOver and Pressed backgrounds");
        var departingRow = (ListViewItem)tree.ContainerFromItem(artist)!;
        var departingOrigin = departingRow.TransformToVisual(page).TransformPoint(new Point());
        double departingHeight = departingRow.ActualHeight;
        double beforeDelete = RowBrightness(WindowCapture.ReadFrame(_window), departingRow, page);
        ((IToggleProvider)new ToggleButtonAutomationPeer(expander)).Toggle();
        Check(!root.IsExpanded && expander.IsChecked == false && vm.VisibleNodes.Count == 2
            && !vm.VisibleNodes.Contains(artist) && !vm.VisibleNodes.Contains(album),
            "UIA collapse updates the model and removes exactly the visible descendants");
        var overlay = (Canvas)page.FindName("FolderMotionOverlay");
        Check(!overlay.IsHitTestVisible && overlay.Children.Count == 2
            && IsNativeExitGroup(((ListViewItem)overlay.Children[0]).Tag, 0)
            && IsNativeExitGroup(((ListViewItem)overlay.Children[1]).Tag, 0),
            "Only add/delete scale and opacity use double speed; native reposition remains independent");
        foreach (ListViewItem ghost in overlay.Children)
        {
            ghost.ApplyTemplate();
            ghost.UpdateLayout();
            var ghostExpander = ghost.FindDescendant<ToggleButton>();
            Check(ghostExpander is not null && ChevronStateBackgroundsAreTransparent(ghostExpander)
                && FluentAnimation.GetPointerOver(ghostExpander) is null && FluentAnimation.GetPressed(ghostExpander) is null,
                "Removal templates keep transparent chevrons in checked, pointer-over and pressed states");
        }
        await Wait(() => tree.ContainerFromItem(artist) is null && tree.ContainerFromItem(album) is null);
        List<double> deleteBrightness = [];
        long deleteDeadline = Environment.TickCount64 + 240;
        while (Environment.TickCount64 < deleteDeadline)
        {
            deleteBrightness.Add(RowBrightnessAt(WindowCapture.ReadFrame(_window), departingOrigin.Y,
                page, departingOrigin.X, departingHeight));
            await Task.Delay(4);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "delete-trace.txt"),
            $"before={beforeDelete:0.0} frames={string.Join(",", deleteBrightness.Select(v => v.ToString("0.0")))}");
        Check(deleteBrightness.Any(v => v > BackgroundBrightness + 1 && v < beforeDelete - 1)
            && deleteBrightness[^1] <= BackgroundBrightness + 1,
            "Removed folder rows fade out before their retained visuals disappear");
        Check(deleteBrightness.All(v => v <= beforeDelete + 1),
            "Batch removal retains original row positions without overlapping bright templates");

        ((IToggleProvider)new ToggleButtonAutomationPeer(expander)).Toggle();
        Check(root.IsExpanded && expander.IsChecked == true && vm.VisibleNodes.SequenceEqual(expanded),
            "UIA expansion restores every previously expanded descendant");
        await Wait(() => tree.ContainerFromItem(artist) is ListViewItem
            && tree.ContainerFromItem(album) is ListViewItem { IsSelected: true });
        await Task.Delay(30);
        var animations = ReadField<object>(motion, "_animations");
        var enterOpacity = ReadField<ScalarKeyFrameAnimation>(animations, "_showOpacity");
        var enterScale = ReadField<Vector3KeyFrameAnimation>(animations, "_showScale");
        var nativeAdded = new Windows.UI.Core.AnimationMetrics.AnimationDescription(
            Windows.UI.Core.AnimationMetrics.AnimationEffect.AddToGrid, Windows.UI.Core.AnimationMetrics.AnimationEffectTarget.Added);
        var nativeOpacity = nativeAdded.Animations.OfType<Windows.UI.Core.AnimationMetrics.OpacityAnimation>().Single();
        var nativeScale = nativeAdded.Animations.OfType<Windows.UI.Core.AnimationMetrics.ScaleAnimation>().Single();
        Check(enterOpacity.Duration == nativeOpacity.Duration / 2 && enterScale.Duration == nativeScale.Duration / 2
            && enterOpacity.DelayTime == TimeSpan.FromMilliseconds(30)
            && enterScale.DelayTime == TimeSpan.FromMilliseconds(30),
            "Add/delete durations are shortened and entrance waits are 30ms; native reposition remains unchanged");

        List<NotifyCollectionChangedEventArgs> bulkChanges = [];
        void OnVisibleNodesChanged(object? sender, NotifyCollectionChangedEventArgs args) => bulkChanges.Add(args);
        vm.VisibleNodes.CollectionChanged += OnVisibleNodesChanged;
        try
        {
            vm.CollapseFolder(root);
            vm.ExpandFolder(root);
        }
        finally
        {
            vm.VisibleNodes.CollectionChanged -= OnVisibleNodesChanged;
        }
        Check(bulkChanges.Count == 4
            && bulkChanges[0].Action == NotifyCollectionChangedAction.Remove
            && bulkChanges[0].OldItems?[0] == artist
            && bulkChanges[0].OldStartingIndex == rootIndex + 1
            && bulkChanges[1].Action == NotifyCollectionChangedAction.Remove
            && bulkChanges[1].OldItems?[0] == album
            && bulkChanges[1].OldStartingIndex == rootIndex + 1
            && bulkChanges[2].Action == NotifyCollectionChangedAction.Add
            && bulkChanges[2].NewItems?[0] == artist
            && bulkChanges[2].NewStartingIndex == rootIndex + 1
            && bulkChanges[3].Action == NotifyCollectionChangedAction.Add
            && bulkChanges[3].NewItems?[0] == album
            && bulkChanges[3].NewStartingIndex == rootIndex + 2,
            "Bulk updates preserve ordered single-item notifications required by WinUI");
        await Wait(() => tree.ContainerFromItem(album) is ListViewItem { IsSelected: true });

        int selectionNotifications = 0;
        void OnSelectedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(FolderViewModel.SelectedNode)) selectionNotifications++;
        }
        vm.PropertyChanged += OnSelectedChanged;
        try
        {
            vm.CollapseFolder(artist);
            vm.CollapseFolder(root);
            vm.ExpandFolder(root);
            Check(selectionNotifications == 0 && !vm.VisibleNodes.Contains(album),
                "Ancestor expansion does not republish a selection hidden by a collapsed parent");
            vm.ExpandFolder(artist);
            await Wait(() => tree.ContainerFromItem(album) is ListViewItem { IsSelected: true });
            Check(selectionNotifications == 1 && vm.VisibleNodes.SequenceEqual(expanded),
                "Expanding the selected branch republishes selection once it becomes visible");
        }
        finally
        {
            vm.PropertyChanged -= OnSelectedChanged;
        }

        for (int i = 0; i < 12; i++)
        {
            vm.CollapseFolder(root);
            vm.ExpandFolder(root);
            if (i % 3 == 0) await Task.Delay(16);
        }
        Check(vm.VisibleNodes.SequenceEqual(expanded), "Rapid expansion reversal never duplicates or drops flat rows");
        await Wait(() => tree.ContainerFromItem(artist) is ListViewItem && tree.ContainerFromItem(album) is ListViewItem);
        await Wait(() => !ReadField<bool>(motion, "_animating") && !ReadField<bool>(motion, "_layoutPending"));
        var artistItem = (ListViewItem)tree.ContainerFromItem(artist)!;
        // 原生让位独立异步完成；自定义增删收尾不代表这一帧已经提交到屏幕。
        await Wait(() => RowBrightness(WindowCapture.ReadFrame(_window), artistItem, page) > BlankThreshold);
        var settledFrame = WindowCapture.ReadFrame(_window);
        double settledBrightness = RowBrightness(settledFrame, artistItem, page);
        await WindowCapture.SaveAsync(settledFrame, Path.Combine(AppContext.BaseDirectory, "tree-motion-settled.png"));
        Check(settledBrightness > BlankThreshold,
            $"Rows remain painted after rapidly reversing native motion: settled={settledBrightness:0.0}");
        Check(ElementCompositionPreview.GetElementVisual(artistItem).ImplicitAnimations is null,
            "Native motion uses explicit animation ownership without implicit layout animations");
        Check(overlay.Children.Count == 0, "Completed motion releases every retained removal template");

        var earlierRoot = vm.VisibleNodes[0];
        var movingVisual = ElementCompositionPreview.GetElementVisual((ListViewItem)tree.ContainerFromItem(root)!);
        vm.ExpandFolder(earlierRoot);
        await Task.Delay(30);
        Check(movingVisual.Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded,
            "Add/delete effects never enable or control Translation on surviving rows");
        Check(movingVisual.ImplicitAnimations is null,
            "Surviving rows keep XAML layout Offset free of competing implicit animations");
        await Task.Delay(750);
        Check(movingVisual.ImplicitAnimations is null,
            "Surviving rows keep native reposition without custom implicit animations");
        Check(!ReadField<bool>(motion, "_animating") && !ReadField<bool>(motion, "_layoutPending")
            && ReadField<System.Collections.IDictionary>(motion, "_snapshot").Count == 0
            && overlay.Children.Count == 0,
            "Motion completion resets every transient row state");
        vm.CollapseFolder(earlierRoot);
    }

    private static bool IsNativeExitGroup(object? value, double delay)
    {
        if (value is not CompositionAnimationGroup group) return false;
        var animations = group.ToArray();
        return animations.Length == 2
            && animations.OfType<ScalarKeyFrameAnimation>().Any(a => a.Target == "Opacity"
                && a.Duration.TotalMilliseconds == 50 && a.DelayTime.TotalMilliseconds == delay)
            && animations.OfType<Vector3KeyFrameAnimation>().Any(a => a.Target == "Scale"
                && a.Duration.TotalMilliseconds == 50 && a.DelayTime.TotalMilliseconds == delay);
    }

    private static T ReadField<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(instance)!;

    /// <summary>
    /// 展开时逐帧采样两行亮度，验证两件事：行出现后不会先亮后暗（闪烁），
    /// 以及过渡结束后两行都真的画满（不会停在透明或半透明状态）。
    /// 只统计位置已稳定的帧——展开瞬间父行还占着子行的目标位置，会读到父行。
    /// </summary>
    private async Task<FadeTrace> MeasureRowRevealAsync(FolderViewModel vm, ListView tree, FolderBrowserNode container,
        FolderBrowserNode first, FolderBrowserNode later, FrameworkElement page)
    {
        var containerItem = (FrameworkElement)tree.ContainerFromItem(container)!;
        double parentY = containerItem.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).Y;
        if (container.IsExpanded)
        {
            vm.CollapseFolder(container);
            await Task.Delay(750);
        }
        var trace = new FadeTrace();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        vm.ExpandFolder(container);
        while (watch.ElapsedMilliseconds < 1100)
        {
            var frame = WindowCapture.ReadFrame(_window);
            long at = watch.ElapsedMilliseconds;
            trace.Record(at, Brightness(frame, tree, first, page), Brightness(frame, tree, later, page));
            if (trace.Settled) break;
            await Task.Delay(2);
        }
        // 采样结束恢复折叠：后续用例依赖「节点尚未展开」这个前提。
        vm.CollapseFolder(container);
        await Task.Delay(750);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fade-trace.txt"), trace.ToString());
        return trace;

        double Brightness(WindowCapture.Frame frame, ListView owner, FolderBrowserNode node, FrameworkElement host)
        {
            if (owner.ContainerFromItem(node) is not FrameworkElement row) return -1;
            // 父行在展开瞬间还占着子行的目标位置，布局完成前采样会读到父行内容。
            double y = row.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point()).Y;
            if (y <= parentY + 1) return -1;
            return RowBrightness(frame, row, host);
        }
    }

    public sealed class FadeTrace
    {
        private readonly List<(long At, double First, double Later)> _samples = [];
        public void Record(long at, double first, double later) => _samples.Add((at, first, later));
        public override string ToString() =>
            $"first={Describe(_samples.Select(x => x.First))} later={Describe(_samples.Select(x => x.Later))} " +
            $"regress={Regressions:0.0}";

        private static string Describe(IEnumerable<double> values) =>
            string.Join(",", values.Where(v => v >= 0).Select(v => v.ToString("0")));

        /// <summary>第一行亮度回退的幅度：出现「先亮后暗」时为正。</summary>
        public double Regressions
        {
            get
            {
                // 行首次被读到时可能仍是展开前渲染的旧帧（截图管线比渲染慢两帧）；
                // 跳过首个采样窗口，只统计行确实出现之后的回落。
                var max = 0.0;
                double? previous = null;
                var seenAt = FirstSeenAt;
                foreach (var (at, value, _) in _samples)
                {
                    if (value < 0 || at < seenAt + 40) continue;
                    if (previous is { } p && value < p) max = Math.Max(max, p - value);
                    previous = value;
                }
                return max;
            }
        }

        /// <summary>第一行首次被读到的时刻。</summary>
        public long FirstSeenAt
        {
            get
            {
                foreach (var sample in _samples)
                    if (sample.First >= 0) return sample.At;
                return 0;
            }
        }

        public double InitialFirst => _samples.FirstOrDefault(x => x.First >= 0).First;
        public double InitialLater => _samples.FirstOrDefault(x => x.Later >= 0).Later;
        public double FinalFirst => _samples.LastOrDefault(x => x.First >= 0).First;
        public double FinalLater => _samples.LastOrDefault(x => x.Later >= 0).Later;
        /// <summary>两行都已画满且读数稳定，可以结束采样。</summary>
        public bool Settled => _samples.Count > 6 && FinalFirst > BlankThreshold && FinalLater > BlankThreshold
            && _samples[^1].First == _samples[^6].First && _samples[^1].Later == _samples[^6].Later;
    }

    /// <summary>
    /// 触发一次展开并采样三件事：新增行是否一出现就在最终位置、缩进是否漂移、行出现的那一刻是否已经画满。
    /// 展开在采样循环内部触发，保证行出现的第一帧就落在采样窗口里。
    /// </summary>
    private async Task<ExpandProbe> ProbeExpandAsync(FolderViewModel vm, ListView tree, FolderBrowserNode container,
        FolderBrowserNode measured, FrameworkElement page, string diagName)
    {
        var probe = new ExpandProbe();
        var samples = new System.Text.StringBuilder();
        var triggered = false;
        var firstFrame = false;
        long firstSeenAt = 0;
        long deadline = Environment.TickCount64 + 1100;
        while (Environment.TickCount64 < deadline)
        {
            if (!triggered)
            {
                var expandWatch = System.Diagnostics.Stopwatch.StartNew();
                vm.ExpandFolder(container);
                expandWatch.Stop();
                probe.ExpandMilliseconds = expandWatch.Elapsed.TotalMilliseconds;
                triggered = true;
            }
            if (tree.ContainerFromItem(measured) is FrameworkElement row)
            {
                var origin = row.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point());
                // 原生缩放会移动绘制边缘；布局槽应保持稳定，新增行也不应额外平移。
                var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(row);
                probe.TrackPosition(slot.X, slot.Y);
                if (ElementCompositionPreview.GetElementVisual(row).Properties.TryGetVector3("Translation", out var translation) == CompositionGetValueStatus.Succeeded)
                    probe.TrackTranslation(translation);
                samples.Append($"{origin.Y:0.0} ");
                if (firstSeenAt == 0) firstSeenAt = Environment.TickCount64;
                // 行出现后 40ms 抓一帧：位移/裁剪/淡入这类过渡此时仍在进行，而未上屏的一帧噪声已经排除。
                if (!firstFrame && Environment.TickCount64 - firstSeenAt >= 40)
                {
                    firstFrame = true;
                    var frame = WindowCapture.ReadFrame(_window);
                    await WindowCapture.SaveAsync(frame, Path.Combine(AppContext.BaseDirectory, diagName.Replace(".txt", ".png")));
                    probe.PaintOnAppear = RowBrightness(frame, row, page);
                }
            }
            else samples.Append("none ");
            await Task.Delay(4);
        }
        if (tree.ContainerFromItem(measured) is FrameworkElement settledRow)
            probe.PaintSettled = RowBrightness(WindowCapture.ReadFrame(_window), settledRow, page);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, diagName),
            $"node={measured.Name} displacement={probe.Displacement:0.0} indent={probe.IndentShift:0.0} " +
            $"paintOnAppear={probe.PaintOnAppear:0.0} paintSettled={probe.PaintSettled:0.0} " +
            $"expandMs={probe.ExpandMilliseconds:0.00}\n{samples}");
        return probe;
    }

    private sealed class ExpandProbe
    {
        private double _minX = double.MaxValue, _maxX = double.MinValue;
        private double _minY = double.MaxValue, _maxY = double.MinValue;
        public void TrackPosition(double x, double y)
        {
            if (x < _minX) _minX = x;
            if (x > _maxX) _maxX = x;
            if (y < _minY) _minY = y;
            if (y > _maxY) _maxY = y;
        }
        public void TrackTranslation(System.Numerics.Vector3 value)
            => TranslationDisplacement = Math.Max(TranslationDisplacement, value.Length());
        public double TranslationDisplacement { get; private set; }
        /// <summary>行在纵向上的位移幅度：非零说明新增行先停在偏移位置再归位。</summary>
        public double Displacement => _maxY > _minY ? _maxY - _minY : 0;
        /// <summary>行在横向上的漂移：非零说明缩进在过渡中被改动。</summary>
        public double IndentShift => _maxX > _minX ? _maxX - _minX : 0;
        /// <summary>行出现那一刻的亮度；低于背景阈值说明行出现了却没画出来。</summary>
        public double PaintOnAppear { get; set; } = -1;
        public double PaintSettled { get; set; } = -1;
        /// <summary>展开事件处理与扁平集合更新的耗时。</summary>
        public double ExpandMilliseconds { get; set; }
    }

    /// <summary>取行区域（DIP 坐标按栅格化比例换算）在渲染帧里的平均亮度。</summary>
    private static double RowBrightness(WindowCapture.Frame frame, FrameworkElement row, FrameworkElement page)
    {
        var origin = row.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point());
        return RowBrightnessAt(frame, origin.Y, page, origin.X, row.ActualHeight);
    }

    /// <summary>按页面坐标取一段行高区域的亮度，用于与行无关的位置采样。</summary>
    private static double RowBrightnessAt(WindowCapture.Frame frame, double y, FrameworkElement page,
        double x = 40, double height = 32)
    {
        var scale = page.XamlRoot.RasterizationScale;
        int x0 = Math.Max(0, (int)((x + 8) * scale)), x1 = Math.Min(frame.Width, (int)((x + 220) * scale));
        int y0 = Math.Max(0, (int)(y * scale)), y1 = Math.Min(frame.Height, (int)((y + height) * scale));
        if (x1 <= x0 || y1 <= y0) return -1;
        double total = 0;
        long count = 0;
        for (int row = y0; row < y1; row++)
            for (int col = x0; col < x1; col++)
            {
                int o = (row * frame.Width + col) * 4;
                total += frame.Pixels[o] + frame.Pixels[o + 1] + frame.Pixels[o + 2];
                count++;
            }
        return count == 0 ? -1 : total / count;
    }

    /// <summary>低于该亮度视为该行没有绘制内容（暗色页面背景约 96）。</summary>
    private const double BlankThreshold = 100;
    /// <summary>暗色页面背景的 RGB 亮度总和。</summary>
    private const int BackgroundBrightness = 96;

    // 真实工况：同名根、深层嵌套、二十多个同级目录；逐层展开后逐行检查是否真的画出来。
    private async Task CheckDeepTreeRenderAsync(Frame frame, AppViewModel app, MusicDatabaseService db,
        FolderViewModel vm, LibraryProjectionService projection)
    {
        _window.AppWindow.Resize(new(1320, 900));
        db.Roots = [new() { Path = @"D:\music", Name = "music" }, new() { Path = @"E:\music", Name = "music" }];
        List<Music> songs = [];
        for (int i = 1; i <= 24; i++) songs.Add(new() { FolderPath = $@"D:\music\专辑{i:00}", Title = $"album {i}" });
        foreach (var name in new[] { "沙中之火OST", "生路OST", "覆潮之下OST", "喧闹法则OST", "午间逸话OST", "傀影与猩红孤钻OST" })
            songs.Add(new() { FolderPath = $@"D:\music\arknight\{name}", Title = name });
        // 多个子目录用于验证同一扁平列表中的深层行。
        foreach (var name in new[] { "剧场版", "TV版", "OVA", "原声集", "广播剧" })
            songs.Add(new() { FolderPath = $@"D:\music\高达\{name}", Title = name });
        const string boxPath = @"D:\music\高达\高达30周年CD-BOX [FLAC]";
        for (int i = 1; i <= 10; i++) songs.Add(new() { FolderPath = $@"{boxPath}\Disc {i}", Title = $"disc {i}" });
        foreach (var name in new[] { "古典", "流行", "流行mp3", "少前", "网易", "影视原声" })
            songs.Add(new() { FolderPath = $@"{boxPath}\{name}", Title = name + " track" });
        songs.Add(new() { FolderPath = @"E:\music\FLAC\无损\2024", Title = "flac" });
        app.State.Library.Replace(songs);
        frame.Navigate(typeof(FolderBrowsePage));
        await Wait(() => frame.Content is FolderBrowsePage { IsLoaded: true } && !vm.IsBusy && vm.Roots.Count == 2);
        var page = (FolderBrowsePage)frame.Content;
        double windowScale = page.XamlRoot.RasterizationScale;
        _window.AppWindow.Resize(new((int)(1320 * windowScale), (int)(900 * windowScale)));
        await Wait(() => page.ActualWidth > 1000);
        var tree = (ListView)page.FindName("FolderTree");
        var root = vm.Roots.Single(n => n.Path == @"D:\music");

        // 用真实 25 同级场景逐帧采样第 1 行与第 11 行，验证原生入场及最终绘制。
        await Task.Delay(300);
        var fadeTrace = await MeasureRowRevealAsync(vm, tree, root,
            root.Children[0], root.Children[Math.Min(10, root.Children.Count - 1)], page);
        Check(fadeTrace.Regressions <= 2,
            $"Revealed rows never dim after appearing: regression={fadeTrace.Regressions:0.0} {fadeTrace}");

        Check(fadeTrace.FinalFirst > BlankThreshold && fadeTrace.FinalLater > BlankThreshold,
            $"Every revealed row ends fully painted: first={fadeTrace.FinalFirst:0.0} later={fadeTrace.FinalLater:0.0}");
        // 整批淡入：范围通知仍应让后一行从背景亮度升到最终亮度，且不能先亮后暗。
        Check(fadeTrace.InitialLater < fadeTrace.FinalLater - 5,
            $"Added rows fade in as a batch after range insertion: later={fadeTrace.InitialLater:0.0}->{fadeTrace.FinalLater:0.0}");

        // 逐帧采样布局位置与缩进，并检查最终绘制。
        var firstChild = root.Children[5];
        var rootProbe = await ProbeExpandAsync(vm, tree, root,
            firstChild, page, "many-rows-diag.txt");
        Check(rootProbe.Displacement <= 1 && rootProbe.TranslationDisplacement <= 0.01,
            $"Added rows keep their layout slots without extra translation during native scaling: displacement={rootProbe.Displacement:0.0} children={root.Children.Count}");
        Check(rootProbe.IndentShift <= 1,
            $"Added rows keep their layout indentation during native scaling: shift={rootProbe.IndentShift:0.0}");
        // 过渡结束后必须完整绘制，不能残留透明或半透明的行。
        Check(rootProbe.PaintSettled > BlankThreshold,
            $"Revealed rows end fully painted: brightness={rootProbe.PaintSettled:0.0}");
        var afterReveal = await MeasureRealizedRowsAsync(page, tree, "many-rows-after.png");
        Check(afterReveal.Length == 0, "No realized row is left transparent after the reveal " + afterReveal);

        // 同级目录的文本缩进必须一致。
        Check(SiblingIndentSpread(page, tree, root.Children) == 0,
            "Sibling folders share the same indent after expanding: " + DescribeIndents(page, tree, root.Children));

        // 大分支收起后只保留根行，再展开验证投影与绘制。
        vm.CollapseFolder(root);
        Check(vm.VisibleNodes.Count == 2, "Collapsing a large branch leaves only the two roots");
        await Wait(() => root.Children.All(n => tree.ContainerFromItem(n) is null));
        await Task.Delay(300);
        var rootProbeAgain = await ProbeExpandAsync(vm, tree, root, firstChild, page, "many-rows-diag2.txt");
        Check(rootProbeAgain.Displacement <= 1 && rootProbeAgain.TranslationDisplacement <= 0.01 && rootProbeAgain.PaintSettled > BlankThreshold,
            $"Re-expanding keeps rows in place and painted: displacement={rootProbeAgain.Displacement:0.0} paint={rootProbeAgain.PaintSettled:0.0}");
        await Task.Delay(800);

        // 深层目录在同一扁平列表中展开，验证布局与绘制。
        var gundam = root.Children.Single(n => n.Name == "高达");
        var nestedProbe = await ProbeExpandAsync(vm, tree, gundam,
            gundam.Children[0], page, "nested-rows-diag.txt");
        Check(nestedProbe.Displacement <= 1 && nestedProbe.TranslationDisplacement <= 0.01,
            $"Nested rows retain their layout slots without extra translation: displacement={nestedProbe.Displacement:0.0} children={gundam.Children.Count}");
        Check(nestedProbe.IndentShift <= 1,
            $"Nested rows keep their indent: shift={nestedProbe.IndentShift:0.0}");
        Check(nestedProbe.PaintSettled > BlankThreshold,
            $"Nested rows end up fully painted: settled={nestedProbe.PaintSettled:0.0}");
        Check(SiblingIndentSpread(page, tree, gundam.Children) == 0,
            "Nested siblings share the same indent after expanding: " + DescribeIndents(page, tree, gundam.Children));
        Check(vm.VisibleNodes.IndexOf(gundam.Children[0]) == vm.VisibleNodes.IndexOf(gundam) + 1,
            "Nested descendants are inserted directly after their parent in the flat list");
        Check(RowContentX(page, tree, gundam.Children[0]) > RowContentX(page, tree, gundam),
            "Nested rows increase their content indentation");

        var boxNode = gundam.Children.Single(n => n.Name.StartsWith("高达30周年"));
        await ExpandAsync(vm, tree, boxNode);
        await Wait(() => boxNode.Children.All(n => tree.ContainerFromItem(n) is not null));
        page.UpdateLayout();
        await Task.Delay(600);

        var firstRound = await MeasureRowsAsync(page, tree, boxNode.Children, "deep-tree-first.png");
        Check(firstRound.Length == 0, "Deep nested folders render every realized row on first expansion " + firstRound);

        // 收起再展开同一节点：用户报告的丢失场景。
        vm.CollapseFolder(boxNode);
        await Wait(() => boxNode.Children.All(n => tree.ContainerFromItem(n) is null));
        await Task.Delay(300);
        await ExpandAsync(vm, tree, boxNode);
        var reExpandRound = await MeasureRowsAsync(page, tree, boxNode.Children, "deep-tree-reexpand.png");
        Check(reExpandRound.Length == 0, "Deep nested folders render every realized row after collapse and re-expand " + reExpandRound);

        // 收起根节点再展开：整棵子树重建，仍要逐行画出来。
        vm.CollapseFolder(root);
        await Wait(() => tree.ContainerFromItem(boxNode) is null);
        await Task.Delay(300);
        await ExpandAsync(vm, tree, root);
        var rebuildRound = await MeasureRowsAsync(page, tree, boxNode.Children, "deep-tree-rebuild.png");
        Check(rebuildRound.Length == 0, "Deep nested folders render every realized row after collapsing the root " + rebuildRound);

        // 滚动复用容器：任何已实现的行都不允许停在未渲染状态。
        if (tree.FindDescendant<ScrollViewer>() is { } scroll)
        {
            scroll.ChangeView(null, 400, null, true);
            await Task.Delay(500);
            var scrolledRound = await MeasureRealizedRowsAsync(page, tree, "deep-tree-scrolled.png");
            Check(scrolledRound.Length == 0, "Scrolling keeps every realized row painted " + scrolledRound);
            Check(SiblingIndentSpread(page, tree, root.Children) == 0,
                "Sibling folders keep the same indent after scrolling: " + DescribeIndents(page, tree, root.Children));
            scroll.ChangeView(null, 0, null, true);
            vm.CollapseFolder(root);
            vm.ExpandFolder(root);
            scroll.ChangeView(null, 400, null, true);
            var immediateScroll = await MeasureRealizedRowsAsync(page, tree, "deep-tree-immediate-scroll.png");
            Check(immediateScroll.Length == 0, "Scrolling during the expansion transition keeps recycled rows painted " + immediateScroll);
        }

        var arknight = root.Children.Single(n => n.Name == "arknight");
        vm.ExpandFolder(arknight);
        tree.SelectedItem = root;
        await Wait(() => vm.SelectedNode == root && !vm.IsBusy);
        vm.CollapseFolder(root);
        vm.ExpandFolder(root);
        var previousRoot = root;
        projection.RequestRefresh();
        await Wait(() => !ReferenceEquals(vm.Roots.Single(n => n.Path == @"D:\music"), previousRoot) && !vm.IsBusy);
        root = vm.Roots.Single(n => n.Path == @"D:\music");
        gundam = root.Children.Single(n => n.Name == "高达");
        arknight = root.Children.Single(n => n.Name == "arknight");
        boxNode = gundam.Children.Single(n => n.Name.StartsWith("高达30周年"));
        Check(root.IsExpanded && gundam.IsExpanded && arknight.IsExpanded && boxNode.IsExpanded,
            "Refresh during a transition preserves every expanded branch");
        Check(gundam.Children.All(vm.VisibleNodes.Contains) && arknight.Children.All(vm.VisibleNodes.Contains)
            && boxNode.Children.All(vm.VisibleNodes.Contains),
            "Rebuild materializes all expanded branches rather than only the selected path");
        Check(vm.VisibleNodes.Distinct().Count() == vm.VisibleNodes.Count && ReferenceEquals(vm.SelectedNode, root),
            "Rebuild keeps flat row identities unique and restores the selected root");
        await Wait(() => tree.ContainerFromItem(root) is ListViewItem { IsSelected: true });
        await Capture(page, "deep-tree-refresh.png");
    }

    private static double RowContentX(FolderBrowsePage page, ListView tree, FolderBrowserNode node)
    {
        if (tree.ContainerFromItem(node) is not ListViewItem item || FindText(item, node.Name) is not { } text)
            return double.NaN;
        return text.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).X;
    }

    /// <summary>同级行文本的布局缩进差（DIP），非零即为错位。</summary>
    private static double SiblingIndentSpread(FolderBrowsePage page, ListView tree, IReadOnlyList<FolderBrowserNode> nodes)
    {
        List<double> indents = [];
        foreach (var node in nodes)
        {
            double x = RowContentX(page, tree, node);
            if (!double.IsNaN(x)) indents.Add(x);
        }
        if (indents.Count < 2) return 0;
        indents.Sort();
        return indents[^1] - indents[0];
    }

    private static string DescribeIndents(FolderBrowsePage page, ListView tree, IReadOnlyList<FolderBrowserNode> nodes)
    {
        List<string> parts = [];
        foreach (var node in nodes)
        {
            double x = RowContentX(page, tree, node);
            if (!double.IsNaN(x)) parts.Add($"{node.Name}@{x:0.0}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>检查面板里所有已实现且完整可见的行是否都绘制了内容。</summary>
    private async Task<string> MeasureRealizedRowsAsync(FolderBrowsePage page, ListView tree, string shotName)
    {
        page.UpdateLayout();
        await Task.Delay(300);
        var shot = WindowCapture.ReadFrame(_window);
        List<string> blank = [];
        int rows = 0;
        var treeOrigin = tree.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point());
        double viewTop = treeOrigin.Y, viewBottom = treeOrigin.Y + tree.ActualHeight;
        if (tree.FindDescendant<ItemsStackPanel>() is { } panel)
            foreach (var child in panel.Children)
            {
                if (child is not ListViewItem item) continue;
                var origin = item.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point());
                // 视口边缘被裁切的行只画出一部分，亮度不能代表是否绘制内容。
                if (origin.Y < viewTop + 4 || origin.Y + item.ActualHeight > viewBottom - 4) continue;
                double brightness = RowBrightness(shot, item, page);
                if (brightness < 0) continue;
                rows++;
                if (brightness < BlankThreshold) blank.Add($"{brightness:0.0}@y={origin.Y:0.0}");
            }
        await WindowCapture.SaveAsync(shot, Path.Combine(AppContext.BaseDirectory, shotName));
        if (rows == 0) return "(no visible row)";
        return blank.Count == 0 ? "" : "[" + string.Join(", ", blank) + "]";
    }

    /// <summary>逐行像素校验：已实现且可见的行必须画出内容，不能只占布局位置。</summary>
    private async Task<string> MeasureRowsAsync(FolderBrowsePage page, ListView tree,
        IReadOnlyList<FolderBrowserNode> nodes, string shotName)
    {
        await Wait(() => nodes.All(n => tree.ContainerFromItem(n) is not null));
        page.UpdateLayout();
        await Task.Delay(600);
        var shot = WindowCapture.ReadFrame(_window);
        List<string> blank = [];
        int checkedRows = 0;
        foreach (var node in nodes)
        {
            if (tree.ContainerFromItem(node) is not ListViewItem item) { blank.Add($"{node.Name}:no-container"); continue; }
            double brightness = RowBrightness(shot, item, page);
            if (brightness < 0) continue;
            checkedRows++;
            if (brightness < BlankThreshold) blank.Add($"{node.Name}:{brightness:0.0}");
        }
        await WindowCapture.SaveAsync(shot, Path.Combine(AppContext.BaseDirectory, shotName));
        if (checkedRows == 0) return "(no visible row)";
        return blank.Count == 0 ? "" : "[" + string.Join(", ", blank) + "]";
    }

    private static async Task ExpandAsync(FolderViewModel vm, ListView tree, FolderBrowserNode node)
    {
        await Wait(() => tree.ContainerFromItem(node) is ListViewItem);
        vm.ExpandFolder(node);
        await Wait(() => !node.HasChildren || node.Children.All(vm.VisibleNodes.Contains));
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(GridSplitter))]
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
        var tree = (ListView)page.FindName("FolderTree");
        var root = vm.Roots[0];
        await Wait(() => tree.ContainerFromItem(root) is ListViewItem);
        vm.ExpandFolder(root);
        await Wait(() => root.Children.Count == names.Length && root.Children.All(n => tree.ContainerFromItem(n) is not null));
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
        bool restored = false;
        for (var i = 0; i < 100 && !restored; i++)
        {
            restored = tree.ContainerFromItem(vm.SelectedNode) is ListViewItem { IsSelected: true };
            if (!restored) await Task.Delay(20);
        }
        if (!restored)
        {
            var selected = vm.SelectedNode;
            var container = selected is null ? null : tree.ContainerFromItem(selected) as ListViewItem;
            var rootNode = vm.Roots[0];
            throw new Exception($"selection restore failed: selected={selected?.Name} treeSelected={(tree.SelectedItem as FolderBrowserNode)?.Name} " +
                $"container={(container is null ? "null" : container.IsSelected.ToString())} roots={vm.Roots.Count} " +
                $"children={selected?.Parent?.Children.Count} rootExpanded={rootNode.IsExpanded} " +
                $"rootVisible={vm.VisibleNodes.Count} rootContainer={tree.ContainerFromItem(rootNode) is not null} " +
                $"transitions={tree.ItemContainerTransitions.Count}");
        }
        Check(vm.SelectedNode!.Location == selectedLocation && vm.CurrentPath == selectedLocation.Path,
            "Tree regeneration restores the same highlighted folder and content");
        await Capture(page, "selection-aligned.png");
    }

    private async Task CheckKeyboardNavigationAsync(ListView tree, FolderViewModel vm,
        FolderBrowserNode artist, FolderBrowserNode album)
    {
        var behaviors = Microsoft.Xaml.Interactivity.Interaction.GetBehaviors(tree)
            .OfType<KeyDownCommandBehavior>().ToDictionary(behavior => behavior.Key);
        Check(behaviors.Count == 2, "XAML attaches left and right key command behaviors");
        var left = behaviors[VirtualKey.Left];
        var right = behaviors[VirtualKey.Right];
        Check(!left.TryExecute(VirtualKey.Right) && vm.SelectedNode == album,
            "A key behavior ignores unrelated keys without executing its command");
        Check(!right.TryExecute(VirtualKey.Right) && vm.SelectedNode == album,
            "Right key remains unhandled for a folder without children");

        Check(left.TryExecute(VirtualKey.Left), "Left key behavior executes for a nested folder");
        await Wait(() => vm.SelectedNode == artist && tree.SelectedItem == artist && !vm.IsBusy);
        Check(vm.CurrentPath == artist.Path, "Left key selects the parent through the ViewModel command");

        Check(right.TryExecute(VirtualKey.Right), "Right key behavior executes for an expanded folder");
        await Wait(() => vm.SelectedNode == album && tree.SelectedItem == album && !vm.IsBusy);
        Check(vm.CurrentPath == album.Path, "Right key selects the first child through the ViewModel command");

        left.TryExecute(VirtualKey.Left);
        await Wait(() => vm.SelectedNode == artist && !vm.IsBusy);
        Check(left.TryExecute(VirtualKey.Left), "Left key behavior executes for an expanded folder");
        await Wait(() => !artist.IsExpanded && !vm.VisibleNodes.Contains(album));
        Check(vm.SelectedNode == artist, "Left key collapses the selected expanded folder");

        Check(right.TryExecute(VirtualKey.Right), "Right key behavior executes for a collapsed folder");
        await Wait(() => artist.IsExpanded && vm.VisibleNodes.Contains(album));
        Check(vm.SelectedNode == artist, "Right key expands the selected collapsed folder");
        Check(right.TryExecute(VirtualKey.Right), "Repeated right key behavior enters the expanded folder");
        await Wait(() => vm.SelectedNode == album && tree.SelectedItem == album && !vm.IsBusy);
        Check(vm.CurrentPath == album.Path, "Repeated right key enters the newly expanded folder");
    }

    private static void SelectNativeRow(ListView tree, FolderBrowserNode node)
    {
        var container = tree.ContainerFromItem(node) as ListViewItem ?? throw new Exception("Folder row is not realized");
        container.IsSelected = true;
        if (!container.IsSelected || !ReferenceEquals(tree.SelectedItem, node))
            throw new Exception("Native row did not become selected");
    }

    private static TextBlock? FindText(DependencyObject parent, string value)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text && text.Text == value) return text;
            if (FindText(child, value) is { } nested) return nested;
        }
        return null;
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
