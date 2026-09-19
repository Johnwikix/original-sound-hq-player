using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

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
        (float)typeof(AnimatedTextBlock).GetMethod("GetHoverOffset", Private).Invoke(c, new object[] { LineValue<float>(c, index, "Distance") });
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
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
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
            panel.Children.Remove(c);
            await Until(() => !c.IsLoaded && Field(c, "_textFormat") == null, "unload cleanup");
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
            typeof(AnimatedTextBlock).GetField("_hoverElapsed", Private).SetValue(c, 0.8 + firstDistance / 36 + 0.4);
            Check(Math.Abs(HoverOffset(c, 0) + firstDistance) < 0.01 && HoverOffset(c, 1) < -firstDistance,
                "One line can pause at its end while the other continues");
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
            await Until(() => !page.TitleControl.IsLoaded && !page.InfoControl.IsLoaded, "page detaches");
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
                    Invoke(unified, "DrawDocument", canvas, drawing);
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
            bool unloaded = false;
            unified.Unloaded += (_, _) => unloaded = true;
            _window.Content = panel;
            await Until(() => unloaded, "unified control Unloaded event completes");
            Check(Field(unified, "_documentLayouts") == null && !(bool)Field(unified, "_isClockRegistered"), "Unified unload releases paragraph layouts and stops clock");
            _window.Content = documentPage;
            await Until(() => unified.IsLoaded && !unified.IsAnimating && Field(unified, "_documentLayouts") != null, "unified reload");
            Check(CanvasCount(documentPage) == 1, "Unified page re-entry retains exactly one canvas");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), $"PASS: {_assertions} assertions; initial auto height={initial}; real WinUI layout/draw/transition/unload/reload.");
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
