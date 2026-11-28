using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace AnimatedTextRegression;

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
    public TestApp()
    {
        UnhandledException += (_, e) =>
        {
            Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "UNHANDLED: " + e.Exception);
        };
        InitializeComponent();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "STARTED");
    }
    private Window _window;
    private int _assertions;
    private const string LongText = "A long music title 标题 with enough words to exceed the available width by a large amount";
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static object Field(AnimatedTextBlock c, string name) => typeof(AnimatedTextBlock).GetField(name, Private).GetValue(c);
    private static void Invoke(AnimatedTextBlock c, string name, params object[] args) => typeof(AnimatedTextBlock).GetMethod(name, Private).Invoke(c, args);
    private static T LineValue<T>(AnimatedTextBlock c, int index, string name)
    {
        object line = ((Array)Field(c, "_hoverLines")).GetValue(index);
        return (T)line.GetType().GetProperty(name).GetValue(line);
    }
    private static float HoverOffset(AnimatedTextBlock c, int index) =>
        (float)typeof(AnimatedTextBlock).GetMethod("GetHoverOffset", Private).Invoke(c,
            new object[] { LineValue<float>(c, index, "Distance"), LineValue<float>(c, index, "LoopDistance") });
    private async Task<string> CheckHdrTitleContrast(AnimatedTextBlock control)
    {
        control.RequestedTheme = ElementTheme.Light;
        control.ClearValue(AnimatedTextBlock.ForegroundProperty);
        control.TextEffect = null;
        control.TextAlignment = TextAlignment.Left;
        control.Document = DocumentLayoutRegressionPage.CreateDocument("Light surface title", "Album\r\nArtist", 44, 30);
        await Until(() => control.ActualTheme == ElementTheme.Light, "the light title theme applies");

        var canvas = (CanvasControl)Field(control, "_canvas");
        var size = canvas.Size;
        var device = canvas.Device;
        var frame = new PlaybackCanvasFrame(device, size, 96, PlaybackHdrColor.FromNits(200, 600));
        using var target = new CanvasRenderTarget(device, (float)size.Width, (float)size.Height, 96,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        using var surface = new Microsoft.Graphics.Canvas.Brushes.CanvasSolidColorBrush(device, Microsoft.UI.Colors.White);
        Invoke(control, "SetScenePresentation", true);

        var lightBrush = ((Microsoft.UI.Xaml.Media.SolidColorBrush)control.Foreground).Color;
        var light = Draw(true);
        control.RequestedTheme = ElementTheme.Dark;
        await Until(() => control.ActualTheme == ElementTheme.Dark, "the dark title theme applies");
        var darkBrush = ((Microsoft.UI.Xaml.Media.SolidColorBrush)control.Foreground).Color;
        var dark = Draw(false);
        var result = $"theme alpha {lightBrush.A}/{darkBrush.A}, light surface title/meta {light.Title:F3}/{light.Metadata:F3}, "
            + $"dark surface title/meta {dark.Title:F3}/{dark.Metadata:F3}";

        Check(lightBrush.R == 0 && light.Title < 0.02f && Math.Abs(light.Metadata - 0.5f) < 0.04f,
            $"Light theme title uses opaque black and metadata retains its paragraph alpha ({result})");
        Check(darkBrush.R == 255 && Math.Abs(dark.Title - 2.5f) < 0.015f && Math.Abs(dark.Metadata - 2f) < 0.04f,
            $"Dark theme title follows the white point after a live theme switch ({result})");

        control.RequestedTheme = ElementTheme.Light;
        control.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(128, 0, 0, 0));
        var custom = Draw(true);
        Check(Math.Abs(custom.Title - 2.5f * (1f - 128f / 255f)) < 0.025f,
            $"HDR retains explicitly supplied foreground alpha ({custom})");

        Invoke(control, "SetScenePresentation", false);
        return result;

        (float Title, float Metadata) Draw(bool lightSurface)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Black);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                if (lightSurface)
                {
                    surface.ColorHdr = new System.Numerics.Vector4(2.5f, 2.5f, 2.5f, 1f);
                    drawing.FillRectangle(new Rect(0, 0, size.Width, size.Height), surface);
                }
                Invoke(control, "DrawInScene", drawing, frame, new Rect(0, 0, size.Width, size.Height), TimeSpan.Zero);
            }

            float title = lightSurface ? 2.5f : 0;
            float metadata = title;
            var pixels = target.GetPixelBytes();
            int width = (int)target.SizeInPixels.Width;
            for (int i = 0; i < pixels.Length; i += 8)
            {
                float red = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(pixels, i));
                if (i / 8 / width < 62)
                    title = lightSurface ? Math.Min(title, red) : Math.Max(title, red);
                else
                    metadata = lightSurface ? Math.Min(metadata, red) : Math.Max(metadata, red);
            }
            return (title, metadata);
        }
    }

    private float CheckProjectedHdrTitle(AnimatedTextBlock control, CanvasRenderTarget target,
        PlaybackCanvasFrame frame, Rect region, float scale, byte[] flatPixels)
    {
        using var host = new NowPlayingCanvas();
        var drawProjected = typeof(NowPlayingCanvas).GetMethod("DrawProjectedHdrTitle", Private)!;
        var projection = PlaybackSpatialProjection.CreatePerspective(
            new Vector2((float)region.Width / 2, (float)region.Height / 2), new Vector3(13, -18, 12), 800);
        var tiltedPixels = DrawSceneTitle(projection);
        bool moved = false;
        float projectedWhite = 0;
        for (int alpha = 6; alpha < flatPixels.Length; alpha += 8)
        {
            float coverage = (float)BitConverter.ToHalf(tiltedPixels, alpha);
            moved |= ((float)BitConverter.ToHalf(flatPixels, alpha) > .5f) != (coverage > .5f);
            if (coverage > .99f)
                projectedWhite = Math.Max(projectedWhite, (float)BitConverter.ToHalf(tiltedPixels, alpha - 6));
        }

        Check(moved, $"The projected {frame.Dpi:G} DPI HDR title moves");
        Check(Math.Abs(projectedWhite - frame.Hdr.White) < .025f,
            $"The projected {frame.Dpi:G} DPI HDR title remains visible at reference white ({projectedWhite:F3})");
        var cachedEffect = Field(control, "_sceneProjectionEffect");
        var cachedSource = Field(control, "_sceneProjectionSource");
        var repeated = DrawSceneTitle(projection);
        Check(cachedEffect is not null && cachedSource is not null
            && ReferenceEquals(cachedEffect, Field(control, "_sceneProjectionEffect"))
            && ReferenceEquals(cachedSource, Field(control, "_sceneProjectionSource"))
            && tiltedPixels.AsSpan().SequenceEqual(repeated),
            $"The {frame.Dpi:G} DPI HDR title reuses its projected tile graph with identical pixels");
        Check(flatPixels.AsSpan().SequenceEqual(DrawSceneTitle(Matrix4x4.Identity))
            && Field(control, "_sceneProjectionEffect") is null && Field(control, "_sceneProjectionSource") is null,
            $"The {frame.Dpi:G} DPI HDR scene title restores its original pixels and releases the projection graph");
        return projectedWhite;

        byte[] DrawSceneTitle(Matrix4x4 matrix)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                drawing.Transform = Matrix3x2.CreateScale(scale);
                drawProjected.Invoke(host, [control, drawing, frame, region, TimeSpan.Zero, matrix]);
            }

            return target.GetPixelBytes();
        }
    }

    private async Task RunProjectedTitleCheckAsync()
    {
        string resultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");
        try
        {
            var title = new AnimatedTextBlock
            {
                Width = 440,
                Height = 160,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                Document = DocumentLayoutRegressionPage.CreateDocument("Static title", "Album\r\nArtist", 44, 30)
            };
            _window = new Window { Content = title, Title = "Projected HDR title regression" };
            _window.Activate();
            await Until(() => title.IsLoaded && Field(title, "_canvas") is CanvasControl { ReadyToDraw: true },
                "projected title canvas initialized");
            var canvas = (CanvasControl)Field(title, "_canvas");
            Invoke(title, "SetScenePresentation", true);
            var white96 = CheckDpi(96, 1);
            var white144 = CheckDpi(144, 1.3f);
            title.PrepareForShutdown();
            File.WriteAllText(resultPath,
                $"PASS: {_assertions} projected HDR title assertions, 96 DPI white {white96:F3}, 144 DPI at 1.3x white {white144:F3}.");

            float CheckDpi(float dpi, float scale)
            {
                var size = canvas.Size;
                var region = new Rect(55, 36, size.Width, size.Height);
                var frame = new PlaybackCanvasFrame(canvas.Device, size, dpi, PlaybackHdrColor.FromNits(200, 600));
                using var target = new CanvasRenderTarget(canvas.Device, (float)(region.Right + 55) * scale, (float)(region.Bottom + 36) * scale,
                    dpi, DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                    drawing.Transform = Matrix3x2.CreateScale(scale);
                    Invoke(title, "DrawProjectedInScene", drawing, frame, region, TimeSpan.Zero, Matrix4x4.Identity);
                }

                return CheckProjectedHdrTitle(title, target, frame, region, scale, target.GetPixelBytes());
            }
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(resultPath, $"FAIL after {_assertions} projected title assertions: {ex}");
        }
        finally
        {
            _window?.Close();
            Exit();
        }
    }

    private string CheckHdrTitle(AnimatedTextBlock control, float dpi, float scale)
    {
        control.RequestedTheme = ElementTheme.Dark;
        SetForeground(Microsoft.UI.Colors.White);
        control.TextEffect = null;
        control.TextAlignment = TextAlignment.Left;
        control.Document = DocumentLayoutRegressionPage.CreateDocument("Static title", "Album\r\nArtist", 44, 30);
        var canvas = (CanvasControl)Field(control, "_canvas");
        var device = canvas.Device;
        var size = canvas.Size;
        var region = new Rect(0, 0, size.Width, size.Height);
        var frame = new PlaybackCanvasFrame(device, size, dpi, PlaybackHdrColor.FromNits(200, 600));
        using var target = new CanvasRenderTarget(device, (float)size.Width * scale, (float)size.Height * scale, dpi,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        Check(target.Dpi == dpi, $"HDR title target DPI is {dpi}");
        int titleBottom = (int)Math.Ceiling(62 * scale * dpi / 96f);
        Invoke(control, "SetScenePresentation", true);
        var defaultPaper = Measure(Draw(TimeSpan.Zero, useFloatPrecision: false));
        var flatPixels = Draw(TimeSpan.Zero);
        var paper = Measure(flatPixels);
        float projectedWhite = CheckProjectedHdrTitle(control, target, frame, region, scale, flatPixels);
        Check(Math.Abs(paper.Title.X - 2.5f) < 0.015f && paper.Metadata is > 1 and <= 2.5f,
            $"Static HDR title and metadata stay at paper white ({paper})");
        Check(canvas.Opacity == 0 && !(bool)Field(control, "_isClockRegistered"),
            "HDR title uses only the scene clock and drawing surface");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(control)!;
        Check(peer.GetAutomationControlType() == AutomationControlType.Text && peer.GetName() == "Static title",
            "The composited title exposes its document title through a Text peer");
        AutomationProperties.SetName(control, "Accessible track title");
        AutomationProperties.SetHelpText(control, "Album and artist");
        Check(peer.GetName() == "Accessible track title" && peer.GetHelpText() == "Album and artist",
            "The title peer respects the current AutomationProperties name and help text");
        control.ClearValue(AutomationProperties.NameProperty);
        control.ClearValue(AutomationProperties.HelpTextProperty);

        BeginWipe("Transition title");
        var transitionPixels = Draw(TimeSpan.FromMilliseconds(320));
        var transition = Measure(transitionPixels);
        Check(Math.Abs(transition.Title.X - paper.Title.X) < .015f && transition.PartialCoverage,
            $"The original wipe animates at reference white ({transition})");
        CheckOriginalDocument(transitionPixels, "wipe");
        var animationStart = (TimeSpan)Field(control, "_animationBeginTime");
        using (var replacement = new CanvasDevice())
        using (var replacementTarget = new CanvasRenderTarget(replacement, (float)size.Width, (float)size.Height, dpi,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied))
        {
            using (var replacementDraw = replacementTarget.CreateDrawingSession())
                Invoke(control, "DrawInScene", replacementDraw, frame with { Device = replacement }, region, TimeSpan.Zero);
            Draw(TimeSpan.Zero);
            Check((TimeSpan)Field(control, "_animationBeginTime") == animationStart && control.IsAnimating,
                "changing the HDR drawing device keeps the title animation phase");
        }
        var tight = Measure(Draw(TimeSpan.Zero, PlaybackHdrColor.FromNits(200, 206)));
        var raised = Measure(Draw(TimeSpan.Zero, PlaybackHdrColor.FromNits(300, 600)));
        Check(Math.Abs(tight.Title.X - paper.Title.X) < .015f && Math.Abs(raised.Title.X - 3.75f) < .015f,
            $"The animated title follows only reference white ({tight.Title}, {raised.Title})");
        var settled = Measure(Draw(TimeSpan.FromSeconds(2)));
        Check(Math.Abs(settled.Title.X - paper.Title.X) < .015f, $"Settled title at reference white ({settled})");

        control.TextEffect = new TextBlurEffect();
        control.Document = DocumentLayoutRegressionPage.CreateDocument("なにが悪い", "Album\r\nArtist", 44, 30);
        Draw(TimeSpan.Zero);
        var blur = Draw(TimeSpan.FromMilliseconds(250));
        var blurValues = Measure(blur);
        Check(blurValues.PartialCoverage && blurValues.Title.X <= frame.Hdr.White + .015f,
            $"The title keeps its original blur and stays within reference white ({blurValues})");
        CheckOriginalDocument(blur, "blur");
        Draw(TimeSpan.FromSeconds(2));

        control.RequestedTheme = ElementTheme.Light;
        SetForeground(Microsoft.UI.Colors.Black);
        control.TextEffect = null;
        control.Document = DocumentLayoutRegressionPage.CreateDocument("Dark title", "Album\r\nArtist", 44, 30);
        Draw(TimeSpan.Zero);
        BeginWipe("Dark title");
        byte[] darkActive = Draw(TimeSpan.FromMilliseconds(320));
        var dark = Measure(darkActive);
        Check(dark.Title.X <= .001f && dark.OpaqueCoverage, $"The default black title retains its contrast ({dark})");
        CheckOriginalDocument(darkActive, "black title");
        Draw(TimeSpan.FromSeconds(2));

        control.RequestedTheme = ElementTheme.Dark;
        var tint = Windows.UI.Color.FromArgb(255, 236, 145, 85);
        SetForeground(tint);
        var tintIdle = Measure(Draw(TimeSpan.Zero));
        BeginWipe("Tinted title");
        var tintActive = Measure(Draw(TimeSpan.FromMilliseconds(320)));
        float hueDrift = System.Numerics.Vector3.Distance(System.Numerics.Vector3.Normalize(tintIdle.Title),
            System.Numerics.Vector3.Normalize(tintActive.Title));
        Check(float.IsFinite(hueDrift) && hueDrift < 0.008f && Math.Abs(tintActive.Title.X - tintIdle.Title.X) < .015f,
            $"Title animation retains the custom hue and luminance ({tintIdle}, {tintActive})");
        Draw(TimeSpan.FromSeconds(2));

        Invoke(control, "SetScenePresentation", false);
        Check(canvas.Opacity == 1 && Field(control, "_sceneTarget") == null && Field(control, "_sceneOutput") == null,
            "The usual title canvas is restored and scene resources are released when the scene is detached");

        if (dpi == 96 && scale == 1)
            CheckDetachedSceneDevice();

        return $"{dpi:G} DPI at {scale:G}x: default static {defaultPaper.Title.X:F3}, FP16 static/wipe/settled {paper.Title.X:F3}/{transition.Title.X:F3}/{settled.Title.X:F3}, tight output peak {tight.Title.X:F3}, 300 nit white {raised.Title.X:F3}, custom hue drift {hueDrift:F5}, projected HDR title {projectedWhite:F3} with identity restore.";

        void SetForeground(Windows.UI.Color color)
            => control.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

        void BeginWipe(string title)
        {
            control.TextEffect = new TextWipeEffect { AnimationDuration = TimeSpan.FromSeconds(1) };
            control.Document = DocumentLayoutRegressionPage.CreateDocument(title, "Album\r\nArtist", 44, 30);
            Draw(TimeSpan.Zero);
        }

        byte[] Draw(TimeSpan elapsed, PlaybackHdrColor? hdr = null, bool useFloatPrecision = true)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                drawing.EffectBufferPrecision = useFloatPrecision ? CanvasBufferPrecision.Precision16Float : null;
                drawing.Transform = System.Numerics.Matrix3x2.CreateScale(scale);
                Invoke(control, "DrawInScene", drawing, hdr is { } color ? frame with { Hdr = color } : frame, region, elapsed);
            }
            return target.GetPixelBytes();
        }

        (System.Numerics.Vector3 Title, float Metadata, bool PartialCoverage, bool OpaqueCoverage) Measure(byte[] pixels)
        {
            int width = (int)target.SizeInPixels.Width;
            var title = System.Numerics.Vector3.Zero;
            float metadata = 0;
            bool transparentEdge = false, opaque = false;
            for (int i = 0; i < pixels.Length; i += 8)
            {
                var rgb = new System.Numerics.Vector3(ReadHalf(pixels, i), ReadHalf(pixels, i + 2), ReadHalf(pixels, i + 4));
                if (i / 8 / width < titleBottom)
                {
                    title = System.Numerics.Vector3.Max(title, rgb);
                    float coverage = ReadHalf(pixels, i + 6);
                    transparentEdge |= coverage is > 0.05f and < 0.95f;
                    opaque |= coverage == 1;
                }
                else
                {
                    metadata = Math.Max(metadata, rgb.X);
                }
            }
            return (title, metadata, transparentEdge, opaque);
        }

        void CheckOriginalDocument(byte[] hdrPixels, string effect)
        {
            // At native scale each FP16 pixel corresponds to one unscaled BGRA8
            // document pixel. Include the transparent margins to detect added halos.
            if (scale != 1) return;
            var document = ((CanvasRenderTarget)Field(control, "_sceneTarget")).GetPixelBytes();
            Check(document.Length * 2 == hdrPixels.Length, $"Original document dimensions for {effect}");
            float alphaError = 0, rgbError = 0;
            for (int i = 0; i < document.Length; i += 4)
            {
                float alpha = document[i + 3] / 255f;
                alphaError = Math.Max(alphaError, Math.Abs(ReadHalf(hdrPixels, i * 2 + 6) - alpha));
                for (int channel = 0; channel < 3; channel++)
                {
                    float straight = alpha == 0 ? 0 : document[i + 2 - channel] / (255f * alpha);
                    float linear = straight <= .04045f ? straight / 12.92f : MathF.Pow((straight + .055f) / 1.055f, 2.4f);
                    float expected = linear * frame.Hdr.White * alpha;
                    rgbError = Math.Max(rgbError, Math.Abs(ReadHalf(hdrPixels, i * 2 + channel * 2) - expected));
                }
            }
            Check(alphaError < .002f && rgbError < .012f,
                $"The HDR {effect} added light or changed the original coverage ({rgbError:F4} RGB, {alphaError:F4} alpha)");
        }

        void CheckDetachedSceneDevice()
        {
            using var isolated = new CanvasDevice();
            using var isolatedTarget = new CanvasRenderTarget(isolated, (float)size.Width, (float)size.Height, 96,
                DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
            control.Foreground = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                GradientStops =
                {
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = Microsoft.UI.Colors.White },
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = Microsoft.UI.Colors.LightGray, Offset = 1 }
                }
            };
            Invoke(control, "SetScenePresentation", true);
            using (var drawing = isolatedTarget.CreateDrawingSession())
            {
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                Invoke(control, "DrawInScene", drawing, frame with { Device = isolated }, region, TimeSpan.Zero);
            }
            Check(ReferenceEquals(Field(control, "_documentDevice"), isolated) && Field(control, "_textBrush") != null,
                "The isolated HDR title uses its device for layout and gradient resources");
            Invoke(control, "SetScenePresentation", false);
            Check(Field(control, "_documentDevice") == null && Field(control, "_documentLayouts") == null
                && Field(control, "_textBrush") == null && Field(control, "_sceneTarget") == null,
                "Leaving HDR drops layouts and brushes owned by the isolated scene device");
        }

        static float ReadHalf(byte[] pixels, int offset)
            => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(pixels, offset));
    }

    private static string DescribeLayout(DependencyObject node, int depth)
    {
        string text = new string(' ', depth * 2) + node.GetType().Name;
        if (node is FrameworkElement element)
            text += $" [{element.Name}] Actual={element.ActualWidth}x{element.ActualHeight}, Desired={element.DesiredSize}, Loaded={element.IsLoaded}, Visibility={element.Visibility}, Slot={Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(element)}";
        text += "\n";
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            text += DescribeLayout(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
        return text;
    }
    private static T ParagraphValue<T>(AnimatedTextBlock c, int index, string name)
    {
        object paragraph = ((Array)Field(c, "_documentLayouts")).GetValue(index);
        return (T)paragraph.GetType().GetField(name).GetValue(paragraph);
    }
    private static int CanvasCount(DependencyObject root)
    {
        int count = root is CanvasControl ? 1 : 0;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            count += CanvasCount(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        return count;
    }
    private static int InkInBand(byte[] pixels, int width, int top, int bottom)
    {
        int count = 0;
        for (int y = top; y < bottom; y++)
            for (int x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] > 8) count++;
        return count;
    }
    private void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _assertions++;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), $"Progress {_assertions}: {message}");
    }
    private static async Task Until(Func<bool> predicate, string message)
    {
        for (int i = 0; i < 150; i++)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new Exception("Timeout: " + message);
    }
    private static void Hover(AnimatedTextBlock c)
    {
        // Inject only pointer presence; layout, draw, shared clock and lifecycle are real WinUI.
        typeof(AnimatedTextBlock).GetField("_isPointerOver", Private).SetValue(c, true);
        ((CanvasControl)Field(c, "_canvas")).Invalidate();
    }
    private async Task CheckEndpoint(AnimatedTextBlock control, ITextEffect effect, AnimatedTextDocument document, float dpi, string label)
    {
        effect.AnimationDuration = TimeSpan.FromSeconds(10);
        effect.DelayPerCluster = TimeSpan.Zero;
        control.TextEffect = effect;
        control.Document = document;
        await Until(() => (AnimatedTextBlockRedrawState)Field(control, "_currentState") == AnimatedTextBlockRedrawState.Animating, "mixed-script endpoint transition starts");
        var endpointDiffs = (System.Collections.Generic.List<TextDiffResult>)Field(control, "_diffResults");
        foreach (var diff in endpointDiffs)
        {
            if (diff.OldGlyphCluster != null) diff.OldGlyphCluster.Progress = 0.99999f;
            if (diff.NewGlyphCluster != null) diff.NewGlyphCluster.Progress = 0.99999f;
        }
        var endpointCanvas = (CanvasControl)Field(control, "_canvas");
        using var animatedEndpoint = new CanvasRenderTarget(endpointCanvas, (float)endpointCanvas.Size.Width, (float)endpointCanvas.Size.Height, dpi);
        using var staticEndpoint = new CanvasRenderTarget(endpointCanvas, (float)endpointCanvas.Size.Width, (float)endpointCanvas.Size.Height, dpi);
        using (var drawing = animatedEndpoint.CreateDrawingSession())
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            Invoke(control, "DrawDocument", endpointCanvas, endpointCanvas.Size.Width, drawing);
        }
        Invoke(control, "SetRedrawState", AnimatedTextBlockRedrawState.Idle, false);
        using (var drawing = staticEndpoint.CreateDrawingSession())
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            Invoke(control, "DrawDocument", endpointCanvas, endpointCanvas.Size.Width, drawing);
        }
        await animatedEndpoint.SaveAsync(Path.Combine(AppContext.BaseDirectory, $"endpoint-{label}-animation.png"), CanvasBitmapFileFormat.Png);
        await staticEndpoint.SaveAsync(Path.Combine(AppContext.BaseDirectory, $"endpoint-{label}-static.png"), CanvasBitmapFileFormat.Png);
        byte[] animatedPixels = animatedEndpoint.GetPixelBytes(), staticPixels = staticEndpoint.GetPixelBytes();
        long alphaDifference = 0, alphaTotal = 0;
        for (int pixel = 3; pixel < animatedPixels.Length; pixel += 4)
        {
            alphaDifference += Math.Abs(animatedPixels[pixel] - staticPixels[pixel]);
            alphaTotal += staticPixels[pixel];
        }
        Check(alphaDifference < alphaTotal * 0.001, $"{label} endpoint matches static positions (alpha difference {alphaDifference / (double)alphaTotal:P4})");
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Array.Exists(Environment.GetCommandLineArgs(), argument => argument == "--check-spatial-title"))
            await RunProjectedTitleCheckAsync();
        else
            await RunRegressionAsync();
    }

    private async Task RunRegressionAsync()
    {
        try
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--inspect-layout") >= 0)
            {
                _window = new Window { Content = new LayoutRegressionPage(), Title = "AnimatedTextBlock layout regression" };
                _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800));
                _window.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
                _window.Activate();
                await Task.Delay(45000);
                _window.Close();
                Exit();
                return;
            }
            var c = new AnimatedTextBlock { Text = "Title 标题", FontSize = 24, TextWrapping = TextWrapping.NoWrap };
            c.Measure(new Size(240, double.PositiveInfinity));
            double initial = c.DesiredSize.Height;
            Check(initial > 0, $"Auto height is {initial}");
            c.FontSize = 48;
            c.Measure(new Size(240, double.PositiveInfinity));
            Check(c.DesiredSize.Height > initial, "Font size must invalidate auto height");
            c.FontSize = 24;
            c.SetValue(AnimatedTextBlock.TextProperty, LongText);
            Check((string)Field(c, "_newText") == LongText, "SetValue must update rendering text");
            c.TextWrapping = TextWrapping.Wrap;
            c.Measure(new Size(120, double.PositiveInfinity));
            Check(c.DesiredSize.Height > initial * 2, "Wrapped text must measure multiple lines");
            c.TextWrapping = TextWrapping.NoWrap;
            c.Padding = new Thickness(7);
            c.Measure(new Size(240, double.PositiveInfinity));
            Check(c.DesiredSize.Height >= initial + 14, "Padding must contribute to height");
            c.Padding = new Thickness(0);
            c.Text = string.Empty;
            c.Measure(new Size(240, double.PositiveInfinity));
            Check(double.IsFinite(c.DesiredSize.Height), "Empty text must have finite height");
            c.Text = LongText;
            c.TextTrimming = TextTrimming.CharacterEllipsis;
            var panel = new StackPanel { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(c);
            _window = new Window { Content = panel };
            _window.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
            _window.Activate();
            await Until(() => c.IsLoaded && Field(c, "_staticTextLayout") != null, "first canvas draw");
            Check(c.ActualHeight > 0, "Auto row without placeholder must be nonzero");
            Check(c.ActualWidth <= 240, "Long text must not expand its viewport");
            Check(!c.IsHoverScrollEnabled, "Hover default must be off");
            Hover(c);
            await Task.Delay(100);
            Check(Field(c, "_hoverLines") == null, "Disabled hover must not scroll");
            c.IsHoverScrollEnabled = true;
            await Until(() => Field(c, "_hoverLines") != null, "hover starts for trimmed text");
            typeof(AnimatedTextBlock).GetField("_hoverElapsed", Private).SetValue(c, 0d);
            c.OnSharedTick(TimeSpan.FromMilliseconds(100));
            Check(HoverOffset(c, 0) < 0, "Hover must start moving immediately after entry");
            c.OnSharedTick(TimeSpan.FromSeconds(2));
            float offset = HoverOffset(c, 0);
            Check(offset < 0, "Hover must advance text horizontally");
            c.TextEffect = new TextFadeEffect { AnimationDuration = TimeSpan.FromMilliseconds(160) };
            c.Text = LongText + " updated";
            Check(Field(c, "_hoverLines") == null, "Text transition resets hover synchronously");
            await Until(() => c.IsAnimating, "transition starts");
            Check(Field(c, "_hoverLines") == null, "Transition and marquee must not overlap");
            await Until(() => !c.IsAnimating && Field(c, "_hoverLines") != null, "hover resumes after transition");
            Invoke(c, "OnPointerExited", c, null);
            Check(Field(c, "_hoverLines") == null && !(bool)Field(c, "_isClockRegistered"), "Exit must dispose hover and stop clock");
            Hover(c);
            await Until(() => Field(c, "_hoverLines") != null, "second hover");
            c.IsHoverScrollEnabled = false;
            Check(Field(c, "_hoverLines") == null && !(bool)Field(c, "_isClockRegistered"), "Disabling must stop clock");
            c.IsHoverScrollEnabled = true;
            c.TextEffect = null;
            c.Text = "short";
            await Until(() => !c.IsAnimating, "short text draw");
            Check(Field(c, "_hoverLines") == null, "Untrimmed text must not scroll");
            c.Text = LongText;
            await Until(() => Field(c, "_hoverLines") != null, "long text scroll");
            panel.Width = 2000;
            await Until(() => c.ActualWidth > 1000 && Field(c, "_hoverLines") == null, "resize clears trimming");
            Check(!(bool)Field(c, "_isClockRegistered"), "Untrimmed resize must stop clock");
            panel.Width = 240;
            c.TextDirection = AnimatedTextBlockTextDirection.RightToLeftThenTopToBottom;
            await Until(() => Field(c, "_hoverLines") != null, "RTL hover");
            offset = HoverOffset(c, 0);
            Check(offset < 0, "RTL starts at the right end of text");
            var retainedFormat = Field(c, "_textFormat");
            panel.Children.Remove(c);
            await Until(() => !c.IsLoaded && !(bool)Field(c, "_isClockRegistered"), "unload stops clock");
            Check(ReferenceEquals(retainedFormat, Field(c, "_textFormat")), "Temporary unload preserves reusable text resources");
            Check(Field(c, "_hoverLines") == null && !(bool)Field(c, "_isClockRegistered"), "Unload stops all rendering");
            panel.Children.Add(c);
            await Until(() => c.IsLoaded && Field(c, "_staticTextLayout") != null, "reload renders again");
            Check(c.ActualHeight > 0, "Reload retains auto height");
            Hover(c);
            await Until(() => Field(c, "_hoverLines") != null, "reload hover");
            c.TextWrapping = TextWrapping.Wrap;
            await Task.Delay(100);
            Check(Field(c, "_hoverLines") == null, "Wrapped text does not use horizontal marquee");
            c.TextWrapping = TextWrapping.NoWrap;
            c.TextDirection = AnimatedTextBlockTextDirection.LeftToRightThenTopToBottom;
            c.TextAlignment = TextAlignment.Center;
            c.Text = LongText + Environment.NewLine + "Artist";
            await Until(() => !c.IsAnimating, "two-line text draw");
            Check(c.ActualHeight > initial * 1.8, "Explicit newline must preserve two-line auto height");
            await Until(() => Field(c, "_hoverLines") != null, "two-line album/artist must scroll when either line is trimmed");
            Check(((Array)Field(c, "_hoverLines")).Length == 2, "Album and artist remain separate lines in one control");
            Check(LineValue<float>(c, 0, "Distance") > 0 && LineValue<float>(c, 1, "Distance") == 0, "Only overflowing album scrolls");
            var artistLayout = LineValue<CanvasTextLayout>(c, 1, "Layout");
            Check(artistLayout.HorizontalAlignment == CanvasHorizontalAlignment.Center && artistLayout.LayoutBounds.X > 0,
                "Short artist retains center alignment");
            var original = (CanvasTextLayout)Field(c, "_staticTextLayout");
            var originalMetrics = original.LineMetrics;
            double expectedBaseline = original.LayoutBounds.Y + originalMetrics[0].Height + originalMetrics[1].Baseline;
            double artistBaseline = LineValue<float>(c, 1, "Y") + artistLayout.LineMetrics[0].Baseline;
            Check(Math.Abs(expectedBaseline - artistBaseline) < 0.01, "Artist baseline must not jump on hover");
            c.OnSharedTick(TimeSpan.FromSeconds(2));
            Check(HoverOffset(c, 0) < 0 && HoverOffset(c, 1) == 0, "Scrolling album must not drag short artist");

            c.Text = "Album" + "\n" + LongText + LongText;
            await Until(() => Field(c, "_hoverLines") != null, "LF artist-only overflow");
            Check(LineValue<float>(c, 0, "Distance") == 0 && LineValue<float>(c, 1, "Distance") > 0, "Artist can scroll independently of album");
            c.Text = LongText + "\r\n" + LongText + LongText;
            await Until(() => Field(c, "_hoverLines") != null, "both lines overflow");
            float firstDistance = LineValue<float>(c, 0, "Distance");
            float secondDistance = LineValue<float>(c, 1, "Distance");
            Check(secondDistance > firstDistance && firstDistance > 0, "Each long line has its own travel distance");
            float firstLoopDistance = LineValue<float>(c, 0, "LoopDistance");
            typeof(AnimatedTextBlock).GetField("_hoverElapsed", Private).SetValue(c, 0d);
            float startOffset = HoverOffset(c, 0);
            typeof(AnimatedTextBlock).GetField("_hoverElapsed", Private).SetValue(c, firstLoopDistance / 36 * 0.5);
            float middleOffset = HoverOffset(c, 0);
            Check(startOffset == 0 && middleOffset < startOffset,
                "LTR marquee keeps moving in one direction");
            typeof(AnimatedTextBlock).GetField("_hoverElapsed", Private).SetValue(c, firstLoopDistance / 36 + 0.01);
            Check(HoverOffset(c, 0) > -firstLoopDistance * 0.1,
                "LTR marquee wraps to the beginning after the full text width");
            c.TextEffect = new TextFadeEffect { AnimationDuration = TimeSpan.FromMilliseconds(160) };
            c.Text = LongText + " album\r\n" + LongText + " artist";
            Check(Field(c, "_hoverLines") == null, "Two-line text change disposes all scrolling layouts");
            await Until(() => !c.IsAnimating && Field(c, "_hoverLines") != null, "two-line hover resumes after animation");
            Invoke(c, "OnPointerExited", c, null);
            Check(Field(c, "_hoverLines") == null && !(bool)Field(c, "_isClockRegistered"), "Two-line pointer exit stops the clock");
            c.TextEffect = null;
            Hover(c);
            c.Text = LongText + "\r\n\r\n🎵 Artist\r\n";
            await Until(() => Field(c, "_hoverLines") != null, "blank and trailing lines");
            Check(((Array)Field(c, "_hoverLines")).Length == 4, "Blank and trailing lines preserve their slots");
            c.TextAlignment = TextAlignment.Right;
            await Until(() => Field(c, "_hoverLines") != null, "right-aligned multiline hover");
            Check(LineValue<CanvasTextLayout>(c, 2, "Layout").HorizontalAlignment == CanvasHorizontalAlignment.Right,
                "Short lines retain right alignment");
            c.TextDirection = AnimatedTextBlockTextDirection.RightToLeftThenTopToBottom;
            await Until(() => Field(c, "_hoverLines") != null, "RTL multiline hover");
            Check(HoverOffset(c, 0) < 0 && HoverOffset(c, 2) == 0, "RTL long line starts at its right edge without moving short lines");
            c.IsHoverScrollEnabled = false;
            Check(Field(c, "_hoverLines") == null && !(bool)Field(c, "_isClockRegistered"), "Disabling disposes every line");
            c.TextDirection = AnimatedTextBlockTextDirection.LeftToRightThenTopToBottom;
            c.TextAlignment = TextAlignment.Center;
            c.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            c.Text = "祝融";
            await Until(() => !c.IsAnimating, "Chinese title settles");
            double chineseHeight = c.ActualHeight;
            c.TextEffect = new TextFadeEffect { AnimationDuration = TimeSpan.FromSeconds(1) };
            c.Text = "All In My Head";
            await Task.Delay(100);
            Check(c.IsAnimating, $"Script change must keep transition running; Chinese height={chineseHeight}, English height={c.ActualHeight}");
            await Until(() => !c.IsAnimating, "resized transition completes");
            c.TextEffect = null;
            c.LineHeight = Math.Ceiling(c.FontSize * 1.4);
            c.Text = "祝融";
            await Until(() => !c.IsAnimating, "uniform Chinese title");
            double fixedHeight = c.ActualHeight;
            var chineseMetrics = ((CanvasTextLayout)Field(c, "_staticTextLayout")).LineMetrics[0];
            c.TextEffect = new TextFadeEffect { AnimationDuration = TimeSpan.FromMilliseconds(500) };
            c.Text = "All In My Head";
            await Task.Delay(80);
            Check(c.IsAnimating && c.ActualHeight == fixedHeight, "Uniform line height preserves layout and transition across scripts");
            await Until(() => !c.IsAnimating, "uniform English title");
            var englishMetrics = ((CanvasTextLayout)Field(c, "_staticTextLayout")).LineMetrics[0];
            Check(Math.Abs(chineseMetrics.Baseline - englishMetrics.Baseline) < 0.01 && Math.Abs(fixedHeight - c.LineHeight) < 0.01,
                "Uniform line height stabilizes baseline as well as control height");
            c.TextEffect = new TextDefaultEffect { AnimationDuration = TimeSpan.FromMilliseconds(500), DelayPerCluster = TimeSpan.Zero };
            c.Text = "祝融\r\nAll In My Head";
            await Task.Delay(80);
            Check(c.IsAnimating && Math.Abs(c.ActualHeight - 2 * fixedHeight) < 0.01,
                "Changing line count must preserve grapheme transition and measure both uniform lines");
            await Until(() => !c.IsAnimating, "grapheme transition after resize completes");
            c.TextEffect = new TextWipeEffect { AnimationDuration = TimeSpan.FromMilliseconds(500) };
            c.Text = "All In My Head";
            await Task.Delay(80);
            Check(c.IsAnimating && c.ActualHeight == fixedHeight, "Two lines to one preserves wipe transition");
            await Until(() => !c.IsAnimating, "wipe after resize completes");
            c.TextEffect = null;
            c.Text = LongText + "\r\n艺术家 Artist";
            c.IsHoverScrollEnabled = true;
            Hover(c);
            await Until(() => Field(c, "_hoverLines") != null, "uniform multiline hover");
            Check(Math.Abs(c.ActualHeight - 2 * fixedHeight) < 0.01 && LineValue<float>(c, 1, "Y") >= fixedHeight - 0.01,
                "Uniform line boxes also apply to independent hover layouts");
            c.IsHoverScrollEnabled = false;
            c.ClearValue(AnimatedTextBlock.LineHeightProperty);
            c.Text = "祝融";
            await Until(() => !c.IsAnimating, "natural line height restored");
            Check(c.ActualHeight == chineseHeight, "Clearing LineHeight restores natural font metrics");
            int bindingErrors = 0;
            void CaptureBindingException(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
            {
                if (e.Exception is ArgumentException || e.Exception is System.Runtime.InteropServices.COMException)
                    bindingErrors++;
            }
            AppDomain.CurrentDomain.FirstChanceException += CaptureBindingException;
            var page = new LayoutRegressionPage();
            _window.Content = page;
            await Task.Delay(1800);
            AppDomain.CurrentDomain.FirstChanceException -= CaptureBindingException;
            Check(bindingErrors == 0, "x:Load must not write an invalid deferred FontSize");
            Check(page.TitleControl != null && page.InfoControl != null && page.TitleControl.IsLoaded && page.InfoControl.IsLoaded,
                "x:Load creates and attaches both animated text controls");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "layout.txt"), DescribeLayout(page, 0));
            Check(page.TitleControl.ActualWidth > 0 && page.TitleControl.ActualHeight >= 62,
                $"Nested grid title remains visible: {page.TitleControl.ActualWidth} x {page.TitleControl.ActualHeight}, FontSize={page.TitleControl.FontSize}, LineHeight={page.TitleControl.LineHeight}, Text={page.TitleControl.Text}");
            Check(page.InfoControl.ActualWidth > 0 && page.InfoControl.ActualHeight >= 84,
                $"Nested grid info remains visible: {page.InfoControl.ActualWidth} x {page.InfoControl.ActualHeight}");
            Check(Field(page.TitleControl, "_staticTextLayout") != null && Field(page.InfoControl, "_staticTextLayout") != null,
                "Both nested-grid controls draw after shared effect completes");
            page.ViewModel.ResizeFonts(56, 34);
            await Until(() => page.TitleControl.ActualHeight == 79 && page.InfoControl.ActualHeight == 96, "responsive font bindings update both rows");
            Check(page.TitleControl.FontSize == 56 && page.InfoControl.FontSize == 34, "Binding continues to follow ViewModel font changes");
            _window.Content = panel;
            await Until(() => !page.TitleControl.IsLoaded && !page.InfoControl.IsLoaded
                && !(bool)Field(page.TitleControl, "_isClockRegistered") && !(bool)Field(page.InfoControl, "_isClockRegistered"), "hidden page stops all clocks");
            _window.Content = page;
            await Until(() => page.TitleControl.IsLoaded && page.TitleControl.ActualHeight == 62 && page.InfoControl.ActualHeight == 84, "page reload restores both rows");
            await Until(() => Field(page.TitleControl, "_staticTextLayout") != null && Field(page.InfoControl, "_staticTextLayout") != null,
                "reloaded page draws both labels");
            Check(page.InfoControl.LineHeight == 42, "Page reload keeps bound line height");
            var documentPage = new DocumentLayoutRegressionPage();
            _window.Content = documentPage;
            await Until(() => documentPage.TextControl?.IsLoaded == true && !documentPage.TextControl.IsAnimating,
                "unified document page loads");
            var unified = documentPage.TextControl;
            Check(unified.ActualHeight == 146 && CanvasCount(documentPage) == 1,
                "Title and two metadata lines use one canvas and their combined intrinsic height");
            var titleLayout = ParagraphValue<CanvasTextLayout>(unified, 0, "NewLayout");
            var infoLayout = ParagraphValue<CanvasTextLayout>(unified, 1, "NewLayout");
            Check(titleLayout.DefaultFontSize == 44 && infoLayout.DefaultFontSize == 30
                && titleLayout.DefaultFontWeight.Weight == 600 && infoLayout.DefaultFontWeight.Weight == 400,
                "Unified control preserves paragraph sizes and weights");
            Check(ParagraphValue<float>(unified, 1, "Y") == 62 && infoLayout.LineCount == 2,
                "Metadata remains two lines below the title");
            unified.TextEffect = null;
            unified.Document = DocumentLayoutRegressionPage.CreateDocument(LongText, LongText + "\r\nArtist", 44, 30);
            Hover(unified);
            await Until(() => Field(unified, "_hoverLines") != null, "formatted paragraph hover");
            Check(((Array)Field(unified, "_hoverLines")).Length == 3 && LineValue<float>(unified, 2, "Distance") == 0,
                "Formatted title and album scroll independently while short artist stays still");
            Check(LineValue<float>(unified, 1, "Opacity") == 0.8f, "Metadata hover preserves its opacity");
            documentPage.ViewModel.SetHoverEnabled(false);
            Check(!unified.IsHoverScrollEnabled && Field(unified, "_hoverLines") == null && !(bool)Field(unified, "_isClockRegistered"),
                "Settings binding immediately disables scrolling and clock activity");
            documentPage.ViewModel.SetHoverEnabled(true);
            await Until(() => Field(unified, "_hoverLines") != null, "settings enables hover again");
            Invoke(unified, "OnPointerExited", unified, null);

            ITextEffect[] effects = [new TextDefaultEffect(), new TextFadeEffect(), new TextWipeEffect(), new TextElasticEffect(),
                new TextZoomEffect(), new TextBlurEffect(), new TextPivotEffect(), new TextMotionBlurEffect()];
            for (int effectIndex = 0; effectIndex < effects.Length; effectIndex++)
            {
                var effect = effects[effectIndex];
                effect.AnimationDuration = TimeSpan.FromSeconds(5);
                effect.DelayPerCluster = TimeSpan.Zero;
                unified.TextEffect = effect;
                unified.Document = DocumentLayoutRegressionPage.CreateDocument("Title " + effectIndex, "Album " + effectIndex + "\r\nArtist " + effectIndex, 44, 30);
                await Until(() => (AnimatedTextBlockRedrawState)Field(unified, "_currentState") == AnimatedTextBlockRedrawState.Animating,
                    effect.GetType().Name + " starts");
                unified.OnSharedTick(TimeSpan.FromSeconds(2.5));
                var canvas = (CanvasControl)Field(unified, "_canvas");
                using var bitmap = new CanvasRenderTarget(canvas, (float)canvas.Size.Width, (float)canvas.Size.Height, 96);
                using (var drawing = bitmap.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    Invoke(unified, "DrawDocument", canvas, canvas.Size.Width, drawing);
                }
                byte[] pixels = bitmap.GetPixelBytes();
                int pixelWidth = (int)bitmap.SizeInPixels.Width;
                Check(InkInBand(pixels, pixelWidth, 0, 62) > 0 && InkInBand(pixels, pixelWidth, 62, 104) > 0
                    && InkInBand(pixels, pixelWidth, 104, 146) > 0, effect.GetType().Name + " paints all three styled rows during transition");
                if (effect is TextFadeEffect)
                {
                    float progress = (float)effect.GetType().GetProperty("Progress", Private).GetValue(effect);
                    Check(progress >= 0.5f && progress < 0.65f, "Fade advances once per shared tick, not once per paragraph");
                    await bitmap.SaveAsync(Path.Combine(AppContext.BaseDirectory, "unified-text.png"), CanvasBitmapFileFormat.Png);
                }
                unified.OnSharedTick(TimeSpan.FromSeconds(6));
                await Until(() => !unified.IsAnimating, "unified effect completes");
            }
            unified.TextEffect = new TextFadeEffect { AnimationDuration = TimeSpan.FromMilliseconds(100) };
            var finalDocument = DocumentLayoutRegressionPage.CreateDocument("Final title", "Final album\r\nFinal artist", 44, 30);
            unified.Document = DocumentLayoutRegressionPage.CreateDocument("Intermediate", "Intermediate\r\nIntermediate", 44, 30);
            unified.Document = finalDocument;
            await Until(() => !unified.IsAnimating && ReferenceEquals(Field(unified, "_renderedDocument"), finalDocument), "coalesced document update");
            Check(ParagraphValue<CanvasTextLayout>(unified, 1, "NewLayout").LineCount == 2, "Atomic document updates retain complete metadata");
            unified.IsActive = false;
            unified.Document = DocumentLayoutRegressionPage.CreateDocument("Final title", "Final album\r\nFinal artist", 44, 30);
            unified.IsActive = true;
            var resumedCanvas = (CanvasControl)Field(unified, "_canvas");
            using (var resumedTarget = new CanvasRenderTarget(resumedCanvas, (float)resumedCanvas.Size.Width, (float)resumedCanvas.Size.Height, 96))
            using (var resumedDraw = resumedTarget.CreateDrawingSession())
                Invoke(unified, "DrawDocument", resumedCanvas, resumedCanvas.Size.Width, resumedDraw);
            Check(!unified.IsAnimating, "reactivating the same title and metadata does not start another animation");
            unified.TextEffect = new TextDefaultEffect { AnimationDuration = TimeSpan.FromSeconds(5), DelayPerCluster = TimeSpan.Zero };
            unified.Document = DocumentLayoutRegressionPage.CreateDocument("Long title", LongText + "\r\nVisible artist", 44, 30);
            await Until(() => (AnimatedTextBlockRedrawState)Field(unified, "_currentState") == AnimatedTextBlockRedrawState.Animating, "trimmed document transition starts");
            var metadataDiffs = ParagraphValue<System.Collections.Generic.List<TextDiffResult>>(unified, 1, "Diffs");
            Check(metadataDiffs.Exists(d => d.NewGlyphCluster?.Characters.Contains('V') == true), "Trimmed album retains following artist in grapheme animation");
            string originalTitle = ParagraphValue<string>(unified, 0, "OldText");
            unified.TextAlignment = TextAlignment.Right;
            await Until(() => ParagraphValue<CanvasTextFormat>(unified, 0, "Format").HorizontalAlignment == CanvasHorizontalAlignment.Right, "alignment changes during animation");
            Check(ParagraphValue<string>(unified, 0, "OldText") == originalTitle && Field(unified, "_diffResults") != null,
                "Layout changes retain animation source and cluster progress data");
            unified.OnSharedTick(TimeSpan.FromSeconds(6));
            await Until(() => !unified.IsAnimating, "changed layout animation completes");
            // Idle 会请求最后一帧静态重建；先等这帧完成，再记录卸载应保留的缓存。
            await Until(() => !(bool)Field(unified, "_documentLayoutDirty") && Field(unified, "_documentLayouts") != null,
                "final static document draw completes before unload");
            bool unloaded = false;
            var retainedDocumentLayouts = Field(unified, "_documentLayouts");
            unified.Unloaded += (_, _) => unloaded = true;
            _window.Content = panel;
            await Until(() => unloaded, "unified control Unloaded event completes");
            Check(ReferenceEquals(retainedDocumentLayouts, Field(unified, "_documentLayouts")) && !(bool)Field(unified, "_isClockRegistered"), "Unified temporary unload preserves paragraph layouts and stops clock: retained=" + ReferenceEquals(retainedDocumentLayouts, Field(unified, "_documentLayouts")) + "; clock=" + Field(unified, "_isClockRegistered"));
            _window.Content = documentPage;
            await Until(() => unified.IsLoaded && !unified.IsAnimating && Field(unified, "_documentLayouts") != null, "unified reload");
            Check(CanvasCount(documentPage) == 1, "Unified page re-entry retains exactly one canvas");
            int endpointIndex = 0;
            foreach (var effect in effects)
            {
                if (effect is TextFadeEffect || effect is TextWipeEffect) continue;
                await CheckEndpoint(unified, effect, DocumentLayoutRegressionPage.CreateDocument(
                    "祝融 All In My Head " + endpointIndex++, "专辑 Album AV office\r\n艺术家 Artist café", 44, 30), 96, effect.GetType().Name);
            }
            await CheckEndpoint(unified, new TextDefaultEffect(), DocumentLayoutRegressionPage.CreateDocument(
                "中英 AVATAR office café", "中文 Album ffi\r\nArtist e\u0301 中英文", 44, 30), 144, "Dpi144");
            unified.TextDirection = AnimatedTextBlockTextDirection.RightToLeftThenTopToBottom;
            await CheckEndpoint(unified, new TextDefaultEffect(), DocumentLayoutRegressionPage.CreateDocument(
                "中文 English שלום", "专辑 Album مرحبا\r\nArtist אבג 中文", 44, 30), 96, "Rtl");
            unified.TextDirection = AnimatedTextBlockTextDirection.LeftToRightThenTopToBottom;
            await CheckEndpoint(unified, new TextDefaultEffect(), DocumentLayoutRegressionPage.CreateDocument(
                LongText, LongText + "\r\nArtist 中文", 44, 30), 96, "Trimmed");
            await CheckEndpoint(unified, new TextDefaultEffect(), DocumentLayoutRegressionPage.CreateDocument(
                "中文 😀 Music", "专辑 🌈 Album\r\nArtist 中文", 44, 30), 96, "Emoji");
            var titleContrast = await CheckHdrTitleContrast(unified);
            var title96 = CheckHdrTitle(unified, 96, 1);
            var title144 = CheckHdrTitle(unified, 144, 1.3f);
            foreach (var retiredControl in new[] { c, page.TitleControl, page.InfoControl, unified })
            {
                retiredControl.PrepareForShutdown();
                retiredControl.PrepareForShutdown();
                Check(Field(retiredControl, "_canvas") == null && Field(retiredControl, "_textFormat") == null
                    && Field(retiredControl, "_documentLayouts") == null && !(bool)Field(retiredControl, "_isClockRegistered"),
                    "Final shutdown releases native resources and the shared clock");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), $"PASS: {_assertions} assertions; initial auto height={initial}; real WinUI layout/draw/transition/unload/reload. HDR title {title96}; {title144}. Theme contrast {titleContrast}.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), $"FAIL after {_assertions} assertions: " + ex);
        }
        _window?.Close();
        Exit();
    }
}
