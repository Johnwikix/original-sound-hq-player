using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Text;
using Windows.UI;
using WinUIMusicPlayer.Controls;
using CompositionShadow = WinUIMusicPlayer.Controls.CompositionShadow;

namespace LocalControlsUiRegression;

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

public sealed partial class ScrollProbe : AutoScrollView
{
    public bool Attached => RedirectVisualAttached;
    public SpriteVisual ScrollingVisual => RootVisual;
    public ContentPresenter Presenter => ChildPresenter!;
    public Grid Viewport => LayoutRoot!;
    public CompositionVisualSurface Source => (CompositionVisualSurface)((CompositionSurfaceBrush)ChildVisualBrush).Surface;
}

internal sealed partial class RegressionApplication : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider _native = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    private readonly IXamlMetadataProvider _local = (IXamlMetadataProvider)Activator.CreateInstance(typeof(Program).Assembly.GetTypes()
        .Single(type => type.FullName!.EndsWith(".XamlMetaDataProvider", StringComparison.Ordinal)))!;
    private readonly List<string> _results = [];
    private readonly string _resultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");
    public IXamlType GetXamlType(Type type) => _local.GetXamlType(type) ?? _native.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _local.GetXamlType(name) ?? _native.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];

    public RegressionApplication() => UnhandledException += (_, args) => File.WriteAllText(_resultPath, "FAIL: " + args.Exception);

    private void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _results.Add("PASS: " + message);
    }

    private static async Task Wait(Func<bool> condition, string message)
    {
        for (int attempt = 0; attempt < 160; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(message);
    }

    private static T? Field<T>(object instance, string name) where T : class =>
        (T?)typeof(CompositionShadow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    private static async Task RemoveAsync(StackPanel panel, FrameworkElement element, FrameworkElement? descendant = null)
    {
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descendantUnloaded = descendant is null ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUnloaded(object sender, RoutedEventArgs args) => unloaded.TrySetResult();
        void OnDescendantUnloaded(object sender, RoutedEventArgs args) => descendantUnloaded!.TrySetResult();
        element.Unloaded += OnUnloaded;
        if (descendant is not null) descendant.Unloaded += OnDescendantUnloaded;
        try
        {
            panel.Children.Remove(element);
            await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 外层事件不是子树的清理屏障；嵌套资源需等待自己的 Unloaded。
            if (descendantUnloaded is not null) await descendantUnloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            element.Unloaded -= OnUnloaded;
            if (descendant is not null) descendant.Unloaded -= OnDescendantUnloaded;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window? window = null;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Themes/Generic.xaml") });
            var panel = new StackPanel { Spacing = 40, Margin = new Thickness(40) };
            var host = new Grid { Background = new SolidColorBrush(Colors.White), RequestedTheme = ElementTheme.Light };
            host.Children.Add(panel);
            window = new Window { Content = host };
            window.AppWindow.Show(false);
            await Wait(() => host.IsLoaded, "window load");
            await ScrollTests(panel, window);
            await BreathingPropertyTests(panel);
            await ShadowTests(panel, window);
            await EdgeColorTests(panel);
            Check(!typeof(AutoScrollView).Assembly.GetReferencedAssemblies().Any(name => name.Name!.StartsWith("DevWinUI")), "No DevWinUI assembly dependency");
            File.WriteAllText(_resultPath, string.Join("\r\n", _results) + "\r\n");
        }
        catch (Exception exception)
        {
            File.WriteAllText(_resultPath, string.Join("\r\n", _results) + "\r\nFAIL: " + exception);
        }
        finally
        {
            window?.Close();
            Exit();
        }
    }

    private async Task BreathingPropertyTests(StackPanel panel)
    {
        var shadow = new CompositionShadow { Content = new Border { Width = 80, Height = 40 }, IsBreathing = true };
        panel.Children.Add(shadow);
        await Wait(() => shadow.IsLoaded && shadow.DropShadow.TryGetAnimationController("BlurRadius") is not null, "breathing property test load");
        var animation = Field<ScalarKeyFrameAnimation>(shadow, "_breathAnimation");
        bool StillBreathing() => shadow.IsBreathing
            && shadow.DropShadow.TryGetAnimationController("BlurRadius") is not null
            && Field<ScalarKeyFrameAnimation>(shadow, "_breathAnimation") == animation;

        shadow.Color = Colors.Red;
        await Task.Delay(50);
        Check(StillBreathing() && shadow.DropShadow.Color == Colors.Red, "Color changes preserve the running breathing animation");
        shadow.OffsetX = 3;
        Check(StillBreathing() && shadow.DropShadow.Offset.X == 3, "OffsetX changes preserve the running breathing animation");
        shadow.OffsetY = 5;
        Check(StillBreathing() && shadow.DropShadow.Offset.Y == 5, "OffsetY changes preserve the running breathing animation");
        shadow.OffsetZ = 7;
        Check(StillBreathing() && shadow.DropShadow.Offset.Z == 7, "OffsetZ changes preserve the running breathing animation");
        shadow.ShadowOpacity = 0.4;
        Check(StillBreathing() && shadow.DropShadow.Opacity == 0.4f, "Opacity changes preserve the running breathing animation");

        string path = Path.Combine(AppContext.BaseDirectory, "breathing-edge-blue.png");
        await CreateImage(path, Colors.Blue);
        shadow.ImageSourceForEdgeColor = path;
        shadow.UseEdgeColorFromImage = true;
        await Wait(() => shadow.Color == Colors.Blue, "breathing image color");
        Check(StillBreathing(), "Asynchronous edge color updates preserve breathing");
        shadow.StopBreath();

        shadow.DropShadow.Offset = new Vector3(12, 13, 14);
        shadow.DropShadow.BlurRadius = 18;
        shadow.DropShadow.Opacity = 0.45f;
        shadow.Color = Colors.Green;
        Check(shadow.DropShadow.Offset == new Vector3(12, 13, 14)
            && shadow.DropShadow.BlurRadius == 18 && shadow.DropShadow.Opacity == 0.45f,
            "Color changes preserve unrelated caller-configured native values");
        shadow.OffsetX = 4;
        Check(shadow.DropShadow.Offset == new Vector3(4, 13, 14)
            && shadow.DropShadow.BlurRadius == 18 && shadow.DropShadow.Opacity == 0.45f,
            "OffsetX changes preserve the other native axes, blur and opacity");
        shadow.OffsetY = 6;
        Check(shadow.DropShadow.Offset == new Vector3(4, 6, 14), "OffsetY changes preserve the other native axes");
        shadow.OffsetZ = 8;
        Check(shadow.DropShadow.Offset == new Vector3(4, 6, 8), "OffsetZ changes preserve the other native axes");
        shadow.ShadowOpacity = 0.3;
        Check(shadow.DropShadow.BlurRadius == 18 && shadow.DropShadow.Offset == new Vector3(4, 6, 8),
            "Opacity changes preserve unrelated native blur and offset");
        shadow.BlurRadius = 21;
        Check(shadow.DropShadow.BlurRadius == 21 && shadow.DropShadow.Opacity == 0.3f
            && shadow.DropShadow.Offset == new Vector3(4, 6, 8), "Explicit blur changes update only the native blur radius");
        await RemoveAsync(panel, shadow);
    }

    private async Task ScrollTests(StackPanel panel, Window window)
    {
        var text = new TextBlock { Text = "Local scrolling control — long artist and song name with a repeating composition surface", FontSize = 24, Foreground = new SolidColorBrush(Colors.Black), TextTrimming = TextTrimming.CharacterEllipsis };
        var scroll = new ScrollProbe { Child = text, Width = 210, IsPlaying = false, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(scroll);
        await Wait(() => scroll.IsLoaded && scroll.ActualWidth > 0, "scroll load");
        Check(!scroll.Attached && scroll.Source.SourceVisual is null, "Stopped content uses the original XAML visual");
        scroll.IsPlaying = true;
        await Wait(() => scroll.Attached, "scroll start");
        Check(scroll.Presenter.ActualWidth > scroll.Viewport.ActualWidth && scroll.Source.SourceVisual is not null, "Overflow is measured unbounded and redirected");
        Check(scroll.ScrollingVisual.Children.Count == 2 && scroll.ScrollingVisual.TryGetAnimationController("Offset.X") is not null, "Two copies and a compositor animation are attached");
        await Task.Delay(150);
        var before = WindowCapture.ReadFrame(window);
        await Task.Delay(300);
        var after = WindowCapture.ReadFrame(window);
        await WindowCapture.SaveAsync(before, Path.Combine(AppContext.BaseDirectory, "scroll-before.png"));
        await WindowCapture.SaveAsync(after, Path.Combine(AppContext.BaseDirectory, "scroll-after.png"));
        Check(!before.Pixels.SequenceEqual(after.Pixels), "Actual composition frames move");
        scroll.Spacing = 45;
        scroll.ScrollingPixelsPreSecond = 90;
        Check(scroll.ScrollingVisual.TryGetAnimationController("Offset.X") is not null, "Spacing and speed can change during scrolling");
        scroll.IsPlaying = false;
        await Wait(() => !scroll.Attached, "scroll stop");
        Check(scroll.Source.SourceVisual is null && scroll.ScrollingVisual.Offset == Vector3.Zero, "Stop releases the source and resets the offset");
        scroll.IsPlaying = true;
        await Wait(() => scroll.Attached, "scroll restart");
        await RemoveAsync(panel, scroll);
        Check(!scroll.Attached && scroll.Source.SourceVisual is null && scroll.ScrollingVisual.TryGetAnimationController("Offset.X") is null, "Unload stops animation and releases the source");
        panel.Children.Add(scroll);
        await Wait(() => scroll.Attached, "scroll reload");
        Check(scroll.ScrollingVisual.TryGetAnimationController("Offset.X") is not null, "Reload restores scrolling without activation");
        scroll.Child = new TextBlock { Text = "short" };
        await Wait(() => !scroll.Attached, "short child replacement");
        Check(scroll.Source.SourceVisual is null, "Short replacement does not keep scrolling");
        scroll.Child = new TextBlock();
        await Task.Delay(100);
        Check(!scroll.Attached, "Empty content does not create a zero-duration animation");
        scroll.Child = text;
        await Wait(() => scroll.Attached, "long child replacement");
        scroll.Width = 2000;
        await Wait(() => !scroll.Attached, "viewport grow");
        scroll.Width = 210;
        await Wait(() => scroll.Attached, "viewport shrink");
        var template = scroll.Template;
        scroll.Template = (ControlTemplate)XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:controls='using:WinUIMusicPlayer.Controls' TargetType='controls:AutoScrollView'><Grid /></ControlTemplate>");
        scroll.ApplyTemplate();
        await Task.Delay(50);
        Check(!scroll.Attached, "Missing template parts do not mark the source as attached");
        scroll.Template = template;
        scroll.ApplyTemplate();
        await Wait(() => scroll.Attached, "template restore");
        Check(scroll.Source.SourceVisual is not null, "Replacing the template reattaches the new source");
        await WindowCapture.CaptureAsync(window, "scroll.png");
        await RemoveAsync(panel, scroll);
    }

    private async Task ShadowTests(StackPanel panel, Window window)
    {
        var text = new TextBlock { Text = "Composition 字形阴影", FontSize = 34 };
        var inner = new CompositionShadow { Content = text, BlurRadius = 12, Color = Colors.Black, ShadowOpacity = 0.8 };
        var outer = new CompositionShadow { Content = inner, BlurRadius = 16 };
        var glyph = text.GetAlphaMask();
        inner.Mask = glyph;
        outer.Mask = glyph;
        panel.Children.Add(outer);
        await Wait(() => outer.IsLoaded && text.ActualWidth > 0, "shadow load");
        Check(inner.Mask == glyph && outer.Mask == glyph && inner.Visual.Size.X == (float)text.ActualWidth, "Nested shadows share a caller-owned glyph mask");
        Check(inner.GetDropShadow() == inner.DropShadow && inner.compositor == inner.Visual.Compositor, "Upstream composition accessors are preserved");
        inner.OffsetX = 3;
        inner.OffsetY = 5;
        inner.OffsetZ = 2;
        Check(inner.DropShadow.Offset == new Vector3(3, 5, 2) && inner.DropShadow.BlurRadius == 12, "Shadow dependency properties update native values");
        await Task.Delay(150);
        await WindowCapture.CaptureAsync(window, "glyph-shadow.png");
        await RemoveAsync(panel, outer, inner);
        Check(inner.DropShadow.Mask is null, "Unload detaches the shared mask without disposing it");
        panel.Children.Add(outer);
        await Wait(() => inner.IsLoaded, "glyph reload");
        Check(inner.Mask == glyph && outer.Mask == glyph, "Reload restores custom masks");
        await RemoveAsync(panel, outer, inner);

        var content = new Border { Width = 150, Height = 90, Background = new SolidColorBrush(Colors.CornflowerBlue) };
        var shadow = new CompositionShadow { Content = content, Width = 150, Height = 90, UseCornerRadius = true, CornerRadius = 18 };
        var originalClip = ElementCompositionPreview.GetElementVisual(content).Compositor.CreateInsetClip();
        ElementCompositionPreview.GetElementVisual(content).Clip = originalClip;
        panel.Children.Add(shadow);
        await Wait(() => shadow.IsLoaded && shadow.ActualWidth > 0, "rounded shadow load");
        Check(Field<CompositionGeometry>(shadow, "_maskGeometry") is CompositionRoundedRectangleGeometry rectangle && rectangle.CornerRadius.X == 18, "Corner radius creates the rounded mask and clip");
        var geometry = Field<CompositionGeometry>(shadow, "_maskGeometry");
        shadow.CornerRadius = 24;
        Check(Field<CompositionGeometry>(shadow, "_maskGeometry") == geometry, "Radius changes reuse the native geometry");
        shadow.IsRounded = true;
        Check(Field<CompositionGeometry>(shadow, "_maskGeometry") is CompositionEllipseGeometry, "Circular mode replaces rounded-rectangle geometry");
        shadow.Mask = glyph;
        Check(shadow.Mask != glyph, "Circular mode retains upstream precedence over custom masks");
        shadow.IsRounded = false;
        Check(shadow.Mask == glyph && ElementCompositionPreview.GetElementVisual(content).Clip == originalClip, "Custom masks restore the previous content clip");
        shadow.Mask = null;
        Check(Field<CompositionGeometry>(shadow, "_maskGeometry") is CompositionRoundedRectangleGeometry, "Clearing the custom mask restores configured corners");
        shadow.IsBreathing = true;
        Check(Field<ScalarKeyFrameAnimation>(shadow, "_breathAnimation") is not null, "Breathing starts on a loaded control");
        await RemoveAsync(panel, shadow);
        Check(Field<ScalarKeyFrameAnimation>(shadow, "_breathAnimation") is null && Field<CompositionGeometry>(shadow, "_maskGeometry") is null, "Unload stops breathing and releases geometry resources");
        Check(ElementCompositionPreview.GetElementVisual(content).Clip == originalClip, "Unload restores the caller-owned clip");
        panel.Children.Add(shadow);
        await Wait(() => shadow.IsLoaded, "rounded shadow reload");
        Check(Field<ScalarKeyFrameAnimation>(shadow, "_breathAnimation") is not null, "Reload resumes breathing");
        shadow.IsBreathing = false;
        Check(Field<ScalarKeyFrameAnimation>(shadow, "_breathAnimation") is null && shadow.DropShadow.BlurRadius == shadow.BlurRadius, "Stopping breathing restores the configured radius");
        shadow.StartBreath(breathDuration: TimeSpan.FromMilliseconds(800));
        Check(Field<ScalarKeyFrameAnimation>(shadow, "_breathAnimation")!.Duration == TimeSpan.FromMilliseconds(800), "Direct breathing API accepts an explicit duration");
        shadow.StopBreath();
        await WindowCapture.CaptureAsync(window, "rounded-shadow.png");
        await RemoveAsync(panel, shadow);
        originalClip.Dispose();
    }

    private async Task EdgeColorTests(StackPanel panel)
    {
        string redPath = Path.Combine(AppContext.BaseDirectory, "edge-red.png");
        string bluePath = Path.Combine(AppContext.BaseDirectory, "edge-blue.png");
        await CreateImage(redPath, Colors.Red);
        await CreateImage(bluePath, Colors.Blue);
        Check(await CompositionShadowImageHelper.GetEdgeColorAsync(redPath, default) == Colors.Red, "Image perimeter average ignores differently colored center pixels");
        Check(await CompositionShadowImageHelper.GetEdgeColorAsync(new Uri("ms-appx:///edge-blue.png"), default) == Colors.Blue, "Package-style URI resolves in an unpackaged host");
        Check(await CompositionShadowImageHelper.GetEdgeColorAsync("edge-red.png", default) == Colors.Red, "Relative image paths resolve against the application directory");
        var shadow = new CompositionShadow { Content = new Border { Width = 40, Height = 40 }, ImageSourceForEdgeColor = redPath, UseEdgeColorFromImage = true };
        panel.Children.Add(shadow);
        await Wait(() => shadow.Color == Colors.Red, "initial edge sampling");
        Check(shadow.Color == Colors.Red, "Enabling image color before load applies it after load");
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        var accepted = server.AcceptTcpClientAsync();
        shadow.ImageSourceForEdgeColor = new Uri($"http://127.0.0.1:{port}/delayed.png");
        using var client = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        var network = client.GetStream();
        var request = new byte[4096];
        await network.ReadAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        shadow.ImageSourceForEdgeColor = new Uri(bluePath);
        await Wait(() => shadow.Color == Colors.Blue, "replacement image color");
        try
        {
            var png = await File.ReadAllBytesAsync(redPath);
            await network.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n"));
            await network.WriteAsync(png);
        }
        catch (IOException) { }
        await Task.Delay(150);
        Check(shadow.Color == Colors.Blue, "A late response cannot overwrite a newer image color");
        shadow.ImageSourceForEdgeColor = Path.Combine(AppContext.BaseDirectory, "missing.png");
        await Wait(() => shadow.Color == Colors.Black, "missing image fallback");
        Check(shadow.Color == Colors.Black, "Image failure is observed and falls back to black");
        shadow.ImageSourceForEdgeColor = redPath;
        await Wait(() => shadow.Color == Colors.Red, "retry after failure");
        shadow.UseEdgeColorFromImage = false;
        Check(shadow.Color == Colors.Black, "Disabling edge sampling restores upstream black fallback");
        await RemoveAsync(panel, shadow);
        Check(Field<CancellationTokenSource>(shadow, "_edgeColorCancellation") is null, "Unload leaves no pending image color request");
    }

    private static async Task CreateImage(string path, Color edge)
    {
        using var bitmap = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 8, 6, 96);
        using (var drawing = bitmap.CreateDrawingSession())
        {
            drawing.Clear(edge);
            drawing.FillRectangle(1, 1, 6, 4, Colors.Lime);
        }
        await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
    }
}
