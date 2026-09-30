using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

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
    private readonly IXamlMetadataProvider _metadata = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _metadata.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private static readonly FieldInfo LinesField = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static List<RenderLyricsLine> Lines(LyricsRenderCoordinator coordinator) => (List<RenderLyricsLine>)LinesField.GetValue(coordinator)!;
    private static List<LyricLine> Lyrics(string text) =>
    [new() { StartMs = 0, EndMs = 10000, Words = [new() { Word = text, StartMs = 0, DurationMs = 10000 }] }];
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "result.txt");
        File.WriteAllText(output, "RUNNING\n");
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var canvas = new CanvasAnimatedControl();
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        var window = new Window { Content = canvas, Title = "Lyrics native resource regression" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(650, 500));
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
        using var releaseFrame = new ManualResetEventSlim();
        var heldFrame = new TaskCompletionSource<RenderLyricsLine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedDraw = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holdFirstFrame = true;
        int frames = 0;
        canvas.CreateResources += (_, _) => coordinator.OnCreateResources();
        canvas.Update += (sender, e) =>
        {
            try { coordinator.OnUpdate(sender, e); }
            catch (Exception ex) { renderFailure.TrySetResult(ex); }
        };
        canvas.Draw += (sender, e) =>
        {
            try
            {
                e.DrawingSession.Clear(Microsoft.UI.Colors.Transparent);
                coordinator.OnDraw(sender, e.DrawingSession);
                Interlocked.Increment(ref frames);
                if (Lines(coordinator).FirstOrDefault() is { PrimaryText: "Published while paused", CachedFill: not null })
                    pausedDraw.TrySetResult();
                if (holdFirstFrame && Lines(coordinator).FirstOrDefault()?.CachedFill is not null)
                {
                    holdFirstFrame = false;
                    heldFrame.TrySetResult(Lines(coordinator)[0]);
                    if (!releaseFrame.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("UI did not release the drawing frame");
                }
            }
            catch (Exception ex) { renderFailure.TrySetResult(ex); }
        };
        try
        {
            coordinator.Attach();
            UILyricsBus.Publish(Lyrics("Original active frame"));
            IsPlayingBus.Publish(true);
            window.Activate();
            var oldLine = await heldFrame.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(!canvas.HasGameLoopThreadAccess, "The publisher must be on the UI thread");
            UILyricsBus.Publish(Lyrics("Intermediate song"));
            UILyricsBus.Publish(Lyrics("Latest song"));
            Check(oldLine.CachedFill is not null && oldLine.PrimaryTextLayout is not null,
                "Active CanvasCommandList/text layout was disposed on the UI thread before the rendering frame finished");
            releaseFrame.Set();
            await canvas.RunOnGameLoopThreadAsync(() => { });
            await Task.Delay(100);
            await canvas.RunOnGameLoopThreadAsync(() =>
            {
                Check(Lines(coordinator).Single().PrimaryText == "Latest song", "The next frame must use the latest publication");
                Check(oldLine.CachedFill is null && oldLine.PrimaryTextLayout is null, "Retired native resources must be released");
            });
            await canvas.RunOnGameLoopThreadAsync(() => MotionChecks.Run(canvas));
            await canvas.RunOnGameLoopThreadAsync(() => HighlightChecks.Run(canvas));
            await DesktopHighlightChecks.RunAsync();
            var timedLyrics = await PlaybackTimingChecks.LoadAsync();
            await canvas.RunOnGameLoopThreadAsync(() => PlaybackTimingChecks.Run(canvas, timedLyrics));
            for (int i = 0; i < 300; i++)
            {
                UILyricsBus.Publish(Lyrics("Switch " + i));
                if (i % 3 == 0) await Task.Delay(1);
            }
            canvas.Paused = true;
            UILyricsBus.Publish(Lyrics("Published while paused"));
            await pausedDraw.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canvas.Paused = false;
            UILyricsBus.Publish([]);
            await Task.Delay(100);
            await canvas.RunOnGameLoopThreadAsync(() => Check(!coordinator.HasLyrics, "Empty lyrics must retire the previous song"));
            UILyricsBus.Publish(Lyrics("Before close"));
            await Task.Delay(100);
            RenderLyricsLine? beforeClose = null;
            await canvas.RunOnGameLoopThreadAsync(() => beforeClose = Lines(coordinator).Single());
            UILyricsBus.Publish(Lyrics("Pending at close"));
            coordinator.PrepareForShutdown();
            coordinator.PrepareForShutdown();
            UILyricsBus.Publish(Lyrics("After close"));
            await Task.Delay(50);
            if (renderFailure.Task.IsCompleted) throw await renderFailure.Task;
            Check(beforeClose!.CachedFill is null && beforeClose.PrimaryTextLayout is null && !coordinator.HasLyrics,
                "Shutdown must release active resources and discard pending lyrics");
            Check(frames > 2, "Real Win2D drawing must have run");
            File.WriteAllText(output, $"PASS: actual The Story of Us sidecar/parser/projection and Win2D brave fill, scale/glow/float return before 157681ms, exact row switch and backward seek; main and desktop gap highlight retention with real drawing, completed fill pixels, exact entrance switch, backward seek, paused gap and overlap; shared visible-origin wave, scale/blur/opacity delay, short-line handling, seek/relayout and live CanvasAnimatedControl frame, native cache retirement, 300 rapid publications, paused redraw, empty lyrics and repeated shutdown with pending work; {frames} frames.\n");
        }
        catch (Exception ex) { File.WriteAllText(output, "FAIL: " + ex); }
        finally
        {
            releaseFrame.Set();
            canvas.Paused = true;
            canvas.RemoveFromVisualTree();
            coordinator.PrepareForShutdown();
            window.Close();
            Exit();
        }
    }
}
