using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer;
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
        catch (Exception exception) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: " + exception); }
        finally { _window?.Close(); Exit(); }
    }
    // 真实 TreeView 上的展开/收起 Composition 过渡：入场淡入、收起行位移补间，以及隐式动画到期摘除。
    // 系统关闭动画时跳过，不计为产品缺陷。
    private async Task CheckTreeMotionAsync(FolderBrowsePage page, TreeView tree, FolderViewModel vm,
        FolderBrowserNode root, FolderBrowserNode artist, FolderBrowserNode album)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        // 初始建库 Reset 会暂停过渡 400ms，先等窗口过期再验证。
        await Task.Delay(500);
        var rootContainer = (TreeViewItem)tree.ContainerFromItem(root)!;
        var survivorContainer = (TreeViewItem)tree.ContainerFromItem(vm.Roots[1])!;
        var rootVisual = ElementCompositionPreview.GetElementVisual(rootContainer)!;
        var survivorVisual = ElementCompositionPreview.GetElementVisual(survivorContainer)!;

        // 收起：行离开扁平列表，剩余行挂上位移隐式动画；340ms 后会被摘除，只能轮询捕捉。
        rootContainer.IsExpanded = false;
        bool artistGone = false, albumGone = false, reflowAttached = false, survivorAttached = false;
        for (var i = 0; i < 50 && !(artistGone && albumGone && reflowAttached && survivorAttached); i++)
        {
            artistGone |= tree.ContainerFromItem(artist) is null;
            albumGone |= tree.ContainerFromItem(album) is null;
            reflowAttached |= rootVisual.HasImplicitAnimation(nameof(Visual.Offset));
            survivorAttached |= survivorVisual.HasImplicitAnimation(nameof(Visual.Offset));
            await Task.Delay(10);
        }
        Check(artistGone && albumGone, "Collapsing removes the subtree rows from the flat list");
        Check(reflowAttached && survivorAttached,
            "Collapsing attaches the composition offset reflow to the remaining rows");
        await Task.Delay(700);
        Check(!rootVisual.HasImplicitAnimation(nameof(Visual.Offset)) && !survivorVisual.HasImplicitAnimation(nameof(Visual.Offset)),
            "Reflow implicit animations detach after the collapse transition settles");

        // 再展开：行只做裁剪显露，位置不能动；过渡结束后裁剪必须完全摘除，行恢复全亮。
        rootContainer.IsExpanded = true;
        TreeViewItem? artistItem = null;
        for (var i = 0; i < 100 && artistItem is null; i++)
        {
            artistItem = tree.ContainerFromItem(artist) as TreeViewItem;
            if (artistItem is null) await Task.Delay(5);
        }
        if (artistItem is null) throw new Exception("Expanded row never realized");
        var artistVisual = ElementCompositionPreview.GetElementVisual(artistItem)!;
        var displaced = false;
        var painted = true;
        double restY = double.NaN;
        for (var i = 0; i < 30; i++)
        {
            double y = artistItem.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).Y;
            if (double.IsNaN(restY)) restY = y;
            displaced |= Math.Abs(y - restY) > 1;
            await Task.Delay(8);
        }
        var midFrame = WindowCapture.ReadFrame(_window);
        await WindowCapture.SaveAsync(midFrame, Path.Combine(AppContext.BaseDirectory, "tree-motion-mid.png"));
        painted &= RowBrightness(midFrame, artistItem, page) > BlankThreshold;
        Check(!displaced, "Revealed rows never move: they are laid out directly at their final position");
        Check(painted && artistVisual.Clip is null, "Revealed rows are painted without any clip");
        await Task.Delay(700);
        var settledFrame = WindowCapture.ReadFrame(_window);
        double settledBrightness = RowBrightness(settledFrame, artistItem, page);
        await WindowCapture.SaveAsync(settledFrame, Path.Combine(AppContext.BaseDirectory, "tree-motion-settled.png"));
        Check(settledBrightness > BlankThreshold,
            $"Revealed rows stay fully painted after the transition: settled={settledBrightness:0.0}");
    }

    /// <summary>
    /// 展开时逐帧采样两行亮度，验证两件事：行出现后不会先亮后暗（闪烁），
    /// 以及过渡结束后两行都真的画满（不会停在透明或半透明状态）。
    /// 只统计位置已稳定的帧——展开瞬间父行还占着子行的目标位置，会读到父行。
    /// </summary>
    private async Task<FadeTrace> MeasureRowRevealAsync(TreeView tree, TreeViewItem container, FolderBrowserNode first,
        FolderBrowserNode later, FrameworkElement page)
    {
        // 父行在展开瞬间还占着子行的目标位置：布局完成前采样会读到父行内容，误判成「先亮后暗」。
        double parentY = container.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).Y;
        double? firstLastY = null, laterLastY = null;
        // 必须是折叠状态，否则设 IsExpanded=true 不会触发 Expanding。
        if (container.IsExpanded)
        {
            container.IsExpanded = false;
            await Task.Delay(700);
        }
        var trace = new FadeTrace();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        container.IsExpanded = true;
        for (var i = 0; i < 70; i++)
        {
            var frame = WindowCapture.ReadFrame(_window);
            long at = watch.ElapsedMilliseconds;
            trace.Record(at, Brightness(frame, tree, first, page), Brightness(frame, tree, later, page));
            if (trace.Settled) break;
            await Task.Delay(8);
        }
        // 采样结束恢复折叠：后续用例依赖「节点尚未展开」这个前提。
        container.IsExpanded = false;
        await Task.Delay(400);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fade-trace.txt"), trace.ToString());
        return trace;

        double Brightness(WindowCapture.Frame frame, TreeView owner, FolderBrowserNode node, FrameworkElement host)
        {
            if (owner.ContainerFromItem(node) is not FrameworkElement row) return -1;
            // 行必须已经排到父行下方、且位置上不再变化，否则这一帧读到的还是父行。
            double y = row.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point()).Y;
            if (y <= parentY + 1) return -1;
            ref double? last = ref node == first ? ref firstLastY : ref laterLastY;
            var stable = last is { } previous && Math.Abs(previous - y) < 0.6;
            last = y;
            return stable ? RowBrightness(frame, row, host) : -1;
        }
    }

    public sealed class FadeTrace
    {
        private readonly List<(long At, double First, double Later)> _samples = [];
        public void Record(long at, double first, double later) => _samples.Add((at, first, later));
        public void Append(string text) => _text += text;
        private string _text = "";
        public override string ToString() =>
            $"first={Describe(_samples.Select(x => x.First))} later={Describe(_samples.Select(x => x.Later))} " +
            $"step={StepMilliseconds}ms regress={Regressions:0.0} {_text}";

        private static string Describe(IEnumerable<double> values) =>
            string.Join(",", values.Where(v => v >= 0).Select(v => v.ToString("0")));
        private static readonly double[] Empty = [];

        /// <summary>第一行亮度回退的幅度：出现「先亮后暗」时为正。</summary>
        public double Regressions
        {
            get
            {
                var max = 0.0;
                double? previous = null;
                foreach (var (_, value, _) in _samples)
                {
                    if (value < 0) continue;
                    if (previous is { } p && value < p) max = Math.Max(max, p - value);
                    previous = value;
                }
                return max;
            }
        }

        /// <summary>两行首次达到半亮（106）的时间差；负数说明后一行并没有更晚。</summary>
        public long StepMilliseconds => ReachedAt(x => x.Later) - ReachedAt(x => x.First);

        private long ReachedAt(Func<(long At, double First, double Later), double> pick)
        {
            foreach (var sample in _samples)
            {
                double value = pick(sample);
                if (value >= 106) return sample.At;
            }
            return -1;
        }

        /// <summary>后一行达到最终亮度（-2 容差）的时刻，用来衡量整段过渡的总时长。</summary>
        public long SettleMilliseconds
        {
            get
            {
                var settled = FinalLater - 2;
                foreach (var sample in _samples)
                    if (sample.Later >= settled) return sample.At;
                return -1;
            }
        }

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
    private async Task<ExpandProbe> ProbeExpandAsync(TreeView tree, TreeViewItem container, FolderBrowserNode measured,
        FrameworkElement page, string diagName, FolderBrowserNode? reflowProbe = null)
    {
        var probe = new ExpandProbe();
        var samples = new System.Text.StringBuilder();
        var triggered = false;
        var firstFrame = false;
        long firstSeenAt = 0;
        long deadline = Environment.TickCount64 + 700;
        while (Environment.TickCount64 < deadline)
        {
            if (!triggered)
            {
                var expandWatch = System.Diagnostics.Stopwatch.StartNew();
                container.IsExpanded = true;
                expandWatch.Stop();
                probe.ExpandMilliseconds = expandWatch.Elapsed.TotalMilliseconds;
                triggered = true;
            }
            if (tree.ContainerFromItem(measured) is FrameworkElement row)
            {
                var origin = row.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point());
                probe.TrackPosition(origin.X, origin.Y);
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
            if (reflowProbe is not null && tree.ContainerFromItem(reflowProbe) is TreeViewItem below)
                probe.ReflowAttached |= ElementCompositionPreview.GetElementVisual(below)!.HasImplicitAnimation(nameof(Visual.Offset));
            await Task.Delay(4);
        }
        if (tree.ContainerFromItem(measured) is FrameworkElement settledRow)
            probe.PaintSettled = RowBrightness(WindowCapture.ReadFrame(_window), settledRow, page);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, diagName),
            $"node={measured.Name} displacement={probe.Displacement:0.0} indent={probe.IndentShift:0.0} " +
            $"paintOnAppear={probe.PaintOnAppear:0.0} paintSettled={probe.PaintSettled:0.0} reflow={probe.ReflowAttached} " +
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
        /// <summary>行在纵向上的位移幅度：非零说明新增行先停在偏移位置再归位。</summary>
        public double Displacement => _maxY > _minY ? _maxY - _minY : 0;
        /// <summary>行在横向上的漂移：非零说明缩进在过渡中被改动。</summary>
        public double IndentShift => _maxX > _minX ? _maxX - _minX : 0;
        /// <summary>行出现那一刻的亮度；低于背景阈值说明行出现了却没画出来。</summary>
        public double PaintOnAppear { get; set; } = -1;
        public double PaintSettled { get; set; } = -1;
        public bool ReflowAttached { get; set; }
        /// <summary>展开事件处理（含行面板收集与隐式动画挂载）的耗时。</summary>
        public double ExpandMilliseconds { get; set; }
    }

    /// <summary>取行区域（DIP 坐标按栅格化比例换算）在渲染帧里的平均亮度。</summary>
    private static double RowBrightness(WindowCapture.Frame frame, FrameworkElement row, FrameworkElement page)
    {
        var scale = page.XamlRoot.RasterizationScale;
        var origin = row.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point());
        int x0 = Math.Max(0, (int)((origin.X + 8) * scale)), x1 = Math.Min(frame.Width, (int)((origin.X + 220) * scale));
        int y0 = Math.Max(0, (int)(origin.Y * scale)), y1 = Math.Min(frame.Height, (int)((origin.Y + row.ActualHeight) * scale));
        if (x1 <= x0 || y1 <= y0) return -1;
        double total = 0;
        long count = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * frame.Width + x) * 4;
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
        // 子目录下有多个目录：展开父级后这些行位于父级 TreeViewItem 自己的嵌套面板里。
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
        var tree = (TreeView)page.FindName("FolderTree");
        var root = vm.Roots.Single(n => n.Path == @"D:\music");

        // 用户报告：淡入有闪烁，且逐行阶梯会让总时长随行数变化。现在改成所有行同时淡入。
        // 用真实 25 同级场景逐帧采样第 1 行与第 11 行的亮度序列。
        var fadeTrace = await MeasureRowRevealAsync(tree, (TreeViewItem)tree.ContainerFromItem(root)!,
            root.Children[0], root.Children[Math.Min(10, root.Children.Count - 1)], page);
        Check(fadeTrace.Regressions <= 2,
            $"Revealed rows never dim after appearing: regression={fadeTrace.Regressions:0.0} {fadeTrace}");

        Check(fadeTrace.FinalFirst > BlankThreshold && fadeTrace.FinalLater > BlankThreshold,
            $"Every revealed row ends fully painted: first={fadeTrace.FinalFirst:0.0} later={fadeTrace.FinalLater:0.0}");

        // 用户报告：展开后行先停在偏下位置、几百毫秒才归位（进入动画没走完）。
        // 断言行从出现的第一帧起就在最终位置，且缩进全程不变。
        var firstChild = root.Children[5];
        var rootProbe = await ProbeExpandAsync(tree, (TreeViewItem)tree.ContainerFromItem(root)!,
            firstChild, page, "many-rows-diag.txt");
        Check(rootProbe.Displacement <= 1,
            $"Expanding rows stay at their final position: displacement={rootProbe.Displacement:0.0} children={root.Children.Count}");
        Check(rootProbe.IndentShift <= 1,
            $"Expanding rows keep their indent: shift={rootProbe.IndentShift:0.0}");
        // 每行独立淡入后必须回到完全不透明：历史上透明度动画中断会让行永久停在未渲染状态。
        Check(rootProbe.PaintSettled > BlankThreshold,
            $"Revealed rows end fully painted: brightness={rootProbe.PaintSettled:0.0}");
        var afterReveal = await MeasureRealizedRowsAsync(page, tree, "many-rows-after.png");
        Check(afterReveal.Length == 0, "No realized row is left transparent after the reveal " + afterReveal);

        // 同级目录缩进必须一致：错位说明容器回收后没有重新应用层级缩进。
        Check(SiblingIndentSpread(page, tree, root.Children) == 0,
            "Sibling folders share the same indent after expanding: " + DescribeIndents(page, tree, root.Children));

        // 收起方向：离场行无法淡出（框架同步移除），剩余行必须挂上补间把位置收拢，
        // 否则整块是瞬间跳上去的。
        var manyRootItem = (TreeViewItem)tree.ContainerFromItem(root)!;
        var manyRootVisual = ElementCompositionPreview.GetElementVisual(manyRootItem)!;
        manyRootItem.IsExpanded = false;
        await Task.Delay(60);
        Check(manyRootVisual.HasImplicitAnimation(nameof(Visual.Offset)),
            "Collapsing attaches the offset reflow to the remaining rows");
        await Task.Delay(420);
        Check(!manyRootVisual.HasImplicitAnimation(nameof(Visual.Offset)),
            "The collapse reflow detaches once the rows have settled");
        await Wait(() => root.Children.All(n => tree.ContainerFromItem(n) is null));
        await Task.Delay(600);
        var rootProbeAgain = await ProbeExpandAsync(tree, manyRootItem, firstChild, page, "many-rows-diag2.txt");
        Check(rootProbeAgain.Displacement <= 1 && rootProbeAgain.PaintSettled > BlankThreshold,
            $"Re-expanding keeps rows in place and painted: displacement={rootProbeAgain.Displacement:0.0} paint={rootProbeAgain.PaintSettled:0.0}");
        await Task.Delay(800);

        // 用户报告：子目录下还有多个目录时展开看不到动画。这类行的容器在父级 TreeViewItem
        // 自己的嵌套面板里，必须同样补间位移、同样播放入场。
        var gundam = root.Children.Single(n => n.Name == "高达");
        var belowSibling = root.Children[1];
        var nestedProbe = await ProbeExpandAsync(tree, (TreeViewItem)tree.ContainerFromItem(gundam)!,
            gundam.Children[0], page, "nested-rows-diag.txt", belowSibling);
        Check(nestedProbe.Displacement <= 1,
            $"Nested rows stay at their final position: displacement={nestedProbe.Displacement:0.0} children={gundam.Children.Count}");
        Check(nestedProbe.IndentShift <= 1,
            $"Nested rows keep their indent: shift={nestedProbe.IndentShift:0.0}");
        Check(nestedProbe.PaintSettled > BlankThreshold,
            $"Nested rows end up fully painted: settled={nestedProbe.PaintSettled:0.0}");
        Check(SiblingIndentSpread(page, tree, gundam.Children) == 0,
            "Nested siblings share the same indent after expanding: " + DescribeIndents(page, tree, gundam.Children));
        // 展开点下方的行即使属于另一层面板，也要跟着平滑位移，否则整块是「瞬间跳出来」的观感。
        Check(nestedProbe.ReflowAttached,
            "Rows below a nested expansion attach the offset reflow even across nested panels");

        var boxNode = gundam.Children.Single(n => n.Name.StartsWith("高达30周年"));
        await ExpandAsync(tree, boxNode);
        await Wait(() => boxNode.Children.All(n => tree.ContainerFromItem(n) is not null));
        page.UpdateLayout();
        await Task.Delay(600);

        var firstRound = await MeasureRowsAsync(page, tree, boxNode.Children, "deep-tree-first.png");
        Check(firstRound.Length == 0, "Deep nested folders render every realized row on first expansion " + firstRound);

        // 收起再展开同一节点：用户报告的丢失场景。
        ((TreeViewItem)tree.ContainerFromItem(boxNode)!).IsExpanded = false;
        await Wait(() => boxNode.Children.All(n => tree.ContainerFromItem(n) is null));
        await Task.Delay(500);
        await ExpandAsync(tree, boxNode);
        var reExpandRound = await MeasureRowsAsync(page, tree, boxNode.Children, "deep-tree-reexpand.png");
        Check(reExpandRound.Length == 0, "Deep nested folders render every realized row after collapse and re-expand " + reExpandRound);

        // 收起根节点再展开：整棵子树重建，仍要逐行画出来。
        ((TreeViewItem)tree.ContainerFromItem(root)!).IsExpanded = false;
        await Wait(() => tree.ContainerFromItem(boxNode) is null);
        await Task.Delay(500);
        await ExpandAsync(tree, root);
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
        }
    }

    /// <summary>同级行的布局缩进差（DIP），非零即为错位；缩进来自布局，与位移动画无关。</summary>
    private static double SiblingIndentSpread(FolderBrowsePage page, TreeView tree, IReadOnlyList<FolderBrowserNode> nodes)
    {
        List<double> indents = [];
        foreach (var node in nodes)
            if (tree.ContainerFromItem(node) is TreeViewItem item)
                indents.Add(item.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).X);
        if (indents.Count < 2) return 0;
        indents.Sort();
        return indents[^1] - indents[0];
    }

    private static string DescribeIndents(FolderBrowsePage page, TreeView tree, IReadOnlyList<FolderBrowserNode> nodes)
    {
        List<string> parts = [];
        foreach (var node in nodes)
            if (tree.ContainerFromItem(node) is TreeViewItem item)
                parts.Add($"{node.Name}@{item.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).X:0.0}");
        return string.Join(", ", parts);
    }

    /// <summary>检查面板里所有已实现且完整可见的行是否都绘制了内容。</summary>
    private async Task<string> MeasureRealizedRowsAsync(FolderBrowsePage page, TreeView tree, string shotName)
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
                if (child is not TreeViewItem item) continue;
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
    private async Task<string> MeasureRowsAsync(FolderBrowsePage page, TreeView tree,
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
            if (tree.ContainerFromItem(node) is not TreeViewItem item) { blank.Add($"{node.Name}:no-container"); continue; }
            double brightness = RowBrightness(shot, item, page);
            if (brightness < 0) continue;
            checkedRows++;
            if (brightness < BlankThreshold) blank.Add($"{node.Name}:{brightness:0.0}");
        }
        await WindowCapture.SaveAsync(shot, Path.Combine(AppContext.BaseDirectory, shotName));
        if (checkedRows == 0) return "(no visible row)";
        return blank.Count == 0 ? "" : "[" + string.Join(", ", blank) + "]";
    }

    private static async Task ExpandAsync(TreeView tree, FolderBrowserNode node)
    {
        await Wait(() => tree.ContainerFromItem(node) is TreeViewItem);
        ((TreeViewItem)tree.ContainerFromItem(node)!).IsExpanded = true;
        await Wait(() => node.Children.Count == 0 || node.VisibleChildren.Count == node.Children.Count);
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
