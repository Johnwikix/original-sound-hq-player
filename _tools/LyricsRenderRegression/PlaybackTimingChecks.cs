using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using Windows.Foundation;

/// <summary>Replays the reported song through actual file projection, Win2D layout and word effects.</summary>
internal static class PlaybackTimingChecks
{
    private static readonly MethodInfo Update = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Focus = typeof(LyricsRenderCoordinator).GetField("_currentLineIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static async Task<List<LyricLine>> LoadAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lyrics-word-effects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "song.flac");
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "story-of-us.qrc.txt"), Path.ChangeExtension(path, ".qrc"));
            var resolver = new LyricsRefreshService(new(), new(), new(), new(), NullLogger<LyricsRefreshService>.Instance);
            return await resolver.SetLyrics(new() { Path = path, Duration = TimeSpan.FromSeconds(265) }, default);
        }
        finally { File.Delete(Path.ChangeExtension(path, ".qrc")); Directory.Delete(directory); }
    }

    internal static void Run(CanvasAnimatedControl canvas, List<LyricLine> lyrics)
    {
        long position = 153255;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, false, 1, 0,
                5, 5, 110, 500, false, false, 0.3, 0.5, 0, EasingType.Sine, EaseMode.Out,
                0.33, 120, false, Microsoft.UI.Colors.White));
            UILyricsBus.Publish(lyrics);
            IsPlayingBus.Publish(true);
            using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            var line = lines.Single(row => row.StartMs == 153255);
            int index = lines.IndexOf(line);
            var brave = line.PrimaryRenderSyllables.Single(word => word.Text == "brave");
            Check(Math.Abs(brave.EndMs!.Value - 157381) < 0.000001, "brave must finish 300ms before the next row.");
            double peakScale = 1, peakGlow = 0;
            void Step(long time, double elapsed)
            {
                position = time;
                Update.Invoke(coordinator, [canvas, TimeSpan.FromMilliseconds(elapsed)]);
                using var drawing = target.CreateDrawingSession();
                coordinator.OnDraw(canvas, drawing);
            }
            for (long time = 153263; time <= 157673; time += 8)
            {
                Step(time, 8);
                foreach (var character in brave.ChildrenRenderLyricsChars)
                {
                    peakScale = Math.Max(peakScale, character.ScaleTransition.Value);
                    peakGlow = Math.Max(peakGlow, character.GlowTransition.Value);
                }
            }
            Step(157680, 9);
            Check(peakScale > 1.05 && peakGlow > 1, "The reported long-word scale and glow must actually animate.");
            Check((int)Focus.GetValue(coordinator)! == index && line.IsPlayingLastFrame,
                "The current row must retain highlight until the next entrance.");
            Check(brave.ChildrenRenderLyricsChars.All(character => character.GetPlayProgress(position) == 1 &&
                Math.Abs(character.ScaleTransition.Value - 1) < 0.001 && character.GlowTransition.Value < 0.01 &&
                Math.Abs(character.FloatTransition.Value) < 0.01),
                "brave must complete its fill and return scale/glow/float before the row switches.");
            Step(157681, 1);
            Check((int)Focus.GetValue(coordinator)! == index + 1 && !line.IsPlayingLastFrame,
                "The next row must start at 157681ms without extending the previous entrance.");
            Step(156800, 8);
            Check((int)Focus.GetValue(coordinator)! == index && line.IsPlayingLastFrame &&
                brave.ChildrenRenderLyricsChars.Any(character => character.GetPlayProgress(position) < 1),
                "Seeking back into brave must recover its partial word progress.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
