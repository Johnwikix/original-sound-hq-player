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
            var connection = new WebDavConnectionDialog { XamlRoot = host.XamlRoot };
            await CheckDialogAsync(connection, connection.TestTree, "connection");
            File.WriteAllText(ResultPath, "PASS: both production dialog templates render names and icons; lazy children, multi-selection, and collapse/re-expand remain functional.");
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
