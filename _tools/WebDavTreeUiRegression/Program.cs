using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;

namespace WebDavTreeUiRegression;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new TestApp();
        });
    }
}

public sealed partial class TestApp : Application
{
    private Window? _window;
    private static readonly string ResultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");

    public TestApp()
    {
        UnhandledException += (_, args) =>
        {
            File.WriteAllText(ResultPath, "UNHANDLED: " + args.Exception);
            Environment.Exit(1);
        };
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(ResultPath, "STARTED");
        try
        {
            var host = new Grid();
            _window = new Window { Content = host, Title = "WebDAV TreeView UI regression" };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 1000));
            _window.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
            _window.Activate();
            await UntilAsync(() => host.XamlRoot is not null, "window root");

            var browser = new WebDavBrowserDialog { XamlRoot = host.XamlRoot };
            await CheckDialogAsync(browser, browser.TestTree, "browser");
            await CheckConnectionAsync(host.XamlRoot);
            await CheckLibraryAndSourcesAsync(host);
            File.WriteAllText(ResultPath, "PASS: real checkbox cascade and partial selection, saved roots, HTTP lazy loading; production SQLite scope filtering and identity retention; real ComboBox removal fallback and filters.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(ResultPath, "FAIL: " + ex);
        }
        finally
        {
            _window?.Close();
            Exit();
        }
    }

    private static TreeViewNode Node(string name, bool folder)
    {
        return new TreeViewNode { Content = new WebDavTreeItem("/fixture/" + name, name, folder), HasUnrealizedChildren = folder };
    }

    private static async Task CheckDialogAsync(ContentDialog dialog, TreeView tree, string label)
    {
        tree.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--without-template") >= 0) tree.ItemTemplate = null;
        var root = Node("音乐 / Music", true);
        tree.RootNodes.Add(root);
        var shown = dialog.ShowAsync();
        try
        {
            await UntilAsync(() => HasText(tree, "音乐 / Music"), label + " root name renders");
            Check((tree.ContainerFromNode(root) as TreeViewItem)?.Content is StackPanel, label + " template creates node view");
            Check(HasIcon(tree, "\uE8B7"), label + " folder icon renders");
            root.IsExpanded = true;
            var first = Node("First track.flac", false);
            var second = Node("第二首.mp3", false);
            root.Children.Add(first);
            root.Children.Add(second);
            root.HasUnrealizedChildren = false;
            await UntilAsync(() => HasText(tree, "First track.flac") && HasText(tree, "第二首.mp3"), label + " lazy child names render");
            Check(HasIcon(tree, "\uE8A5"), label + " file icon renders");
            Check(!HasText(tree, "Microsoft.UI.Xaml.Controls.TreeViewNode"), label + " no type-name labels");
            tree.SelectedNodes.Add(second);
            tree.SelectedNodes.Add(first);
            Check(tree.SelectedNodes.Contains(first) && tree.SelectedNodes.Contains(second), label + " multiple nodes select");
            root.IsExpanded = false;
            await UntilAsync(() => tree.ContainerFromNode(first) is null, label + " children collapse");
            root.IsExpanded = true;
            await UntilAsync(() => HasText(tree, "First track.flac") && HasText(tree, "第二首.mp3"), label + " names survive re-expansion");
            await Task.Delay(300); // 等待原生展开动画结束再留存截图。
            await SaveImageAsync(tree, label + "-tree.png");
        }
        finally
        {
            dialog.Hide();
            await shown;
        }
    }

    private static async Task CheckConnectionAsync(XamlRoot xamlRoot)
    {
        const string musicRoot = "/dav/%E9%9F%B3%E4%B9%90/";
        await using var server = new Fixture();
        using var transport = new WebDavTransport();
        var library = new WebDavLibraryService();
        using var vm = new WebDavConnectionViewModel(transport, library, new WebDavSource
        {
            Id = 1, Name = "Fixture", BaseUri = server.Root, Roots = musicRoot
        });
        var dialog = new WebDavConnectionDialog(vm) { XamlRoot = xamlRoot };
        var tree = dialog.TestTree;
        var shown = dialog.ShowAsync();
        try
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            var root = tree.RootNodes[0];
            root.IsExpanded = true;
            var music = root.Children[0];
            await UntilAsync(() => FindCheckBox(tree.ContainerFromNode(music)) is not null, "music checkbox");
            var musicCheck = FindCheckBox(tree.ContainerFromNode(music))!;
            Check(await vm.SaveAsync() && library.Saved?.Roots == musicRoot,
                "restoring the only child must not save its automatically selected parent");
            Check(musicCheck.IsChecked == true, "saved checkbox restored");
            music.IsExpanded = true;
            await UntilAsync(() => music.Children.Count == 2 && FindCheckBox(tree.ContainerFromNode(music.Children[1])) is not null,
                "real lazy loading and child templates");
            var live = FindCheckBox(tree.ContainerFromNode(music.Children[0]))!;
            var studio = FindCheckBox(tree.ContainerFromNode(music.Children[1]))!;
            Check(live.IsChecked == true && studio.IsChecked == true, "lazy children inherit checked parent");
            Toggle(live);
            Check(live.IsChecked == false && studio.IsChecked == true && musicCheck.IsChecked is null,
                "excluding child retains checked sibling and partial parent");
            Check(await vm.SaveAsync() && library.Saved?.Roots == musicRoot + "studio/", "exclude child from parent scope");
            Toggle(live);
            string siblings = musicRoot + "live/\n" + musicRoot + "studio/";
            Check(await vm.SaveAsync() && library.Saved?.Roots == siblings && musicCheck.IsChecked is null,
                "checking every child does not expand explicit scope");
            Toggle(musicCheck);
            Check(live.IsChecked == true && studio.IsChecked == true && await vm.SaveAsync() && library.Saved?.Roots == musicRoot,
                "clicking partial parent explicitly selects subtree");
            Toggle(musicCheck);
            Check(live.IsChecked == false && studio.IsChecked == false && !await vm.SaveAsync(),
                "unchecking parent clears all descendants");
            Toggle(live);
            Check(await vm.SaveAsync() && library.Saved?.Roots == musicRoot + "live/", "select only one directory after entire root");
            music.IsExpanded = false;
            music.IsExpanded = true;
            await UntilAsync(() => FindCheckBox(tree.ContainerFromNode(music.Children[0]))?.IsChecked == true,
                "selection survives re-expansion");
            var rootCheck = FindCheckBox(tree.ContainerFromNode(root))!;
            Toggle(rootCheck);
            Check(musicCheck.IsChecked == true && await vm.SaveAsync() && library.Saved?.Roots == "/dav/",
                "entire root cascades to loaded descendants");
            Toggle(rootCheck);
            Check(!await vm.SaveAsync() && musicCheck.IsChecked == false, "clearing entire root clears subtree");
            await vm.ConnectCommand.ExecuteAsync(null);
            Check(await vm.SaveAsync() && library.Saved?.Roots == musicRoot, "reconnect restores original without broadening");
            await UntilAsync(() => HasText(tree, "音乐") && HasIcon(tree, "\uE8B7"), "reconnected names and icons render");
            await Task.Delay(300); // 等待原生展开动画结束再留存截图。
            await SaveImageAsync(tree, "connection-tree.png");
        }
        finally { dialog.Hide(); await shown; }
    }

    private static void Toggle(CheckBox check)
    {
        var peer = new Microsoft.UI.Xaml.Automation.Peers.CheckBoxAutomationPeer(check);
        ((Microsoft.UI.Xaml.Automation.Provider.IToggleProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)).Toggle();
    }

    private static CheckBox? FindCheckBox(DependencyObject? node)
    {
        if (node is null) return null;
        if (node is CheckBox { Name: "ScanRootCheckBox" } check) return check;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindCheckBox(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private static bool HasText(DependencyObject node, string text)
    {
        if (node is TextBlock block && block.Text == text && block.ActualWidth > 0 && block.ActualHeight > 0) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasText(VisualTreeHelper.GetChild(node, i), text)) return true;
        return false;
    }

    private static bool HasIcon(DependencyObject node, string glyph)
    {
        if (node is FontIcon icon && icon.Glyph == glyph && icon.ActualWidth > 0 && icon.ActualHeight > 0) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasIcon(VisualTreeHelper.GetChild(node, i), glyph)) return true;
        return false;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task UntilAsync(Func<bool> predicate, string message)
    {
        for (int i = 0; i < 150; i++)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(message);
    }

    private static async Task SaveImageAsync(UIElement element, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
