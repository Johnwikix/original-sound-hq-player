using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Model;
using Windows.UI;

namespace FoliaOffscreenRender;

internal static class Program
{
    private static RenderApp? _app;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _app = new RenderApp(args);
        });
    }
}

internal sealed partial class RenderApp : Application
{
    private static readonly string[] ModeNames =
    [
        "classic", "cadenza", "partita", "fume", "tilt", "claddagh", "diorama",
        "pendolo", "sonnet", "tempera", "lumiere",
    ];

    private static readonly string[] FixtureText =
    [
        "迷失在无边际这幽深的森林", "风吹过", "我们慢慢走回去，星河与风一起落在很远很远的地方",
        "光落在晨雾里", "你说夜色会把所有的名字都轻轻藏起来", "晚安",
        "在城市尽头等一场迟到的雨", "把回忆折成纸船放进河流", "轻轻唱 hello again 在无人的街角",
        "我听见远方的海潮一遍一遍拍打着沉默的礁石和那座早已熄灭多年的灯塔",
    ];

    private readonly string _outputDirectory;
    private readonly int _width;
    private readonly int _height;
    private Window? _window;

    public RenderApp(string[] args)
    {
        _outputDirectory = GetArgument(args, "--output")
            ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        _width = GetIntArgument(args, "--width", 1280);
        _height = GetIntArgument(args, "--height", 720);
        Directory.CreateDirectory(_outputDirectory);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // A hidden window gives WinUI a real DispatcherQueue and D3D device while all pixels
            // are rendered into CanvasRenderTarget objects. No desktop window is shown.
            
            _window = new Window();
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(2, 2));
            _window.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
            _window.Activate();

            CanvasDevice device = CanvasDevice.GetSharedDevice();
            for (int mode = 0; mode < ModeNames.Length; mode++)
            {
                foreach (float time in new[] { 0f, 6f, 37.5f })
                {
                    string path = Path.Combine(_outputDirectory, $"{mode:00}-{ModeNames[mode]}-{time:0.0}.png");
                    await RenderFrameAsync(device, mode, time, path);
                }
            }

            File.WriteAllText(Path.Combine(_outputDirectory, "manifest.txt"),
                $"size={_width}x{_height}{Environment.NewLine}modes={ModeNames.Length}{Environment.NewLine}times=0,6,37.5");
            _window.Close();
            Exit();
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(_outputDirectory, "error.txt"), exception.ToString());
            Environment.ExitCode = 1;
            _window?.Close();
            Exit();
        }
    }

    private async System.Threading.Tasks.Task RenderFrameAsync(CanvasDevice device, int mode, float time, string path)
    {
        DesktopLyricsVisualMode visualMode = (DesktopLyricsVisualMode)mode;
        DesktopLyricsTheme theme = new DesktopLyricsTheme(
            "Folia common probe",
            "Deterministic upstream common palette",
            Color.FromArgb(0xFF, 0x09, 0x09, 0x0B),
            Color.FromArgb(0xFF, 0x71, 0x71, 0x7A),
            Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5),
            Color.FromArgb(0xFF, 0x71, 0x71, 0x7A),
            Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5)) with
        { VisualMode = visualMode };

        var style = new DesktopLyricsStyle(
            FontSize: 64,
            FontFamily: "Segoe UI",
            Color: theme.Primary,
            FontWeight: 700,
            ShowTranslation: true,
            ShowPronunciation: false,
            Glow: true,
            CharFloat: true,
            CharScale: true,
            LongSyllableThreshold: 600,
            GlowAmount: 5,
            CharFloatAmount: 5,
            CharScaleAmount: 0.06,
            UseCustomColor: true,
            ShadowAmount: 0)
        { Theme = theme };

        using var renderer = new FoliaLyricsRenderer(createCanvas: false);
        renderer.SetStyle(style);
        renderer.SetLyrics(CreateFixtureLyrics());

        using var target = new CanvasRenderTarget(device, _width, _height, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            renderer.RenderOffscreen(target, drawing, _width, _height, time * 1000f);

        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
    }

    private static List<LyricLine> CreateFixtureLyrics()
    {
        var result = new List<LyricLine>(40);
        const double lineDuration = 3000;
        for (int i = 0; i < 40; i++)
        {
            double start = i * lineDuration;
            string text = FixtureText[i % FixtureText.Length];
            result.Add(new LyricLine
            {
                StartMs = start,
                EndMs = start + lineDuration - 120,
                TransLateText = $"translation {i}",
                Words =
                [
                    new LyricWord
                    {
                        Word = text,
                        StartMs = start,
                        DurationMs = lineDuration - 160,
                    },
                ],
            });
        }
        return result;
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static int GetIntArgument(string[] args, string name, int fallback)
        => int.TryParse(GetArgument(args, name), out int value) && value > 0 ? value : fallback;

}
