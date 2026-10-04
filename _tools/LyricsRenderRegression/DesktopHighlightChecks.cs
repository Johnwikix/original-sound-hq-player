using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.DesktopLyrics;

/// <summary>Checks the production desktop renderer through its UI publication and native drawing callbacks.</summary>
internal static class DesktopHighlightChecks
{
    private static readonly FieldInfo Line = typeof(CanvasLyricsRenderer).GetField("_currentLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Renderer = typeof(CanvasLyricsRenderer).GetField("_lineRenderer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Time = typeof(CanvasLyricsRenderer).GetField("_internalTimeMs", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DisposeLine = typeof(CanvasLyricsRenderer).GetMethod("DisposeCurrentLine", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static async Task RunAsync()
    {
        using var desktop = new CanvasLyricsRenderer();
        var canvas = (CanvasAnimatedControl)desktop.Content;
        var window = new Window { Content = canvas, Title = "Desktop lyric highlight regression" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(650, 400));
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
        desktop.SetStyle(new(40, "Segoe UI", Microsoft.UI.Colors.White, 700,
            false, true, false, false, false, 1000, 5, 5, 110, true, 0));
        desktop.SetIsPlaying(false);
        desktop.SetLyrics([
            new LyricLine
            {
                StartMs = 77396, EndMs = 83675, HighlightEndMs = 96652,
                PronunciationText = "it carries on",
                Words = [new LyricWord { Word = "It carries on", StartMs = 77396, DurationMs = 6279 }]
            },
            new LyricLine
            {
                StartMs = 96652, EndMs = 98000, HighlightEndMs = 100000,
                Words = [new LyricWord { Word = "Next", StartMs = 96652, DurationMs = 1348 }]
            }
        ]);
        try
        {
            window.Activate();
            foreach (long time in new long[] { 83000, 83675, 85000, 96651, 96652, 85000 })
            {
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
                {
                    if ((double)Time.GetValue(desktop)! != time) return;
                    if (Line.GetValue(desktop) is not RenderLyricsLine line || line.CachedFill is null) return;
                    try
                    {
                        double expectedStart = time < 96652 ? 77396 : 96652;
                        var renderer = (LyricsLineRenderer)Renderer.GetValue(desktop)!;
                        if (line.StartMs != expectedStart || !line.IsPlayingLastFrame || !renderer.IsPlaying)
                            throw new InvalidOperationException($"Desktop highlight/selection disappeared or selected the wrong row at {time} ms.");
                        if (line.StartMs == 77396 && line.PronunciationLayer.Layout is null)
                            throw new InvalidOperationException("Desktop renderer must lay out the existing pronunciation track.");
                        if (line.StartMs == 77396 && line.EndMs != 83675)
                            throw new InvalidOperationException("Desktop playback must preserve the original singing end.");
                        completed.TrySetResult();
                    }
                    catch (Exception error) { completed.TrySetException(error); }
                }
                canvas.Draw += OnDraw;
                try
                {
                    desktop.SetPlaybackTime(time);
                    canvas.Invalidate();
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { canvas.Draw -= OnDraw; }
            }
        }
        finally
        {
            await canvas.RunOnGameLoopThreadAsync(() =>
            {
                DisposeLine.Invoke(desktop, null);
            });
            desktop.Dispose();
            canvas.RemoveFromVisualTree();
            window.Close();
        }
    }
}
