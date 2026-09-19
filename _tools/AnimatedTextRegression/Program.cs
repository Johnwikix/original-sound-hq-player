using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using Microsoft.Graphics.Canvas.UI.Xaml;
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
            Check(Field(c, "_hoverTextLayout") == null, "Disabled hover must not scroll");
            c.IsHoverScrollEnabled = true;
            await Until(() => Field(c, "_hoverTextLayout") != null, "hover starts for trimmed text");
            c.OnSharedTick(TimeSpan.FromSeconds(2));
            float offset = (float)typeof(AnimatedTextBlock).GetMethod("GetHoverOffset", Private).Invoke(c, null);
            Check(offset < 0, "Hover must advance text horizontally");
            c.TextEffect = new TextFadeEffect { AnimationDuration = TimeSpan.FromMilliseconds(160) };
            c.Text = LongText + " updated";
            Check(Field(c, "_hoverTextLayout") == null, "Text transition resets hover synchronously");
            await Until(() => c.IsAnimating, "transition starts");
            Check(Field(c, "_hoverTextLayout") == null, "Transition and marquee must not overlap");
            await Until(() => !c.IsAnimating && Field(c, "_hoverTextLayout") != null, "hover resumes after transition");
            Invoke(c, "OnPointerExited", c, null);
            Check(Field(c, "_hoverTextLayout") == null && !(bool)Field(c, "_isClockRegistered"), "Exit must dispose hover and stop clock");
            Hover(c);
            await Until(() => Field(c, "_hoverTextLayout") != null, "second hover");
            c.IsHoverScrollEnabled = false;
            Check(Field(c, "_hoverTextLayout") == null && !(bool)Field(c, "_isClockRegistered"), "Disabling must stop clock");
            c.IsHoverScrollEnabled = true;
            c.TextEffect = null;
            c.Text = "short";
            await Until(() => !c.IsAnimating, "short text draw");
            Check(Field(c, "_hoverTextLayout") == null, "Untrimmed text must not scroll");
            c.Text = LongText;
            await Until(() => Field(c, "_hoverTextLayout") != null, "long text scroll");
            panel.Width = 2000;
            await Until(() => c.ActualWidth > 1000 && Field(c, "_hoverTextLayout") == null, "resize clears trimming");
            Check(!(bool)Field(c, "_isClockRegistered"), "Untrimmed resize must stop clock");
            panel.Width = 240;
            c.TextDirection = AnimatedTextBlockTextDirection.RightToLeftThenTopToBottom;
            await Until(() => Field(c, "_hoverTextLayout") != null, "RTL hover");
            offset = (float)typeof(AnimatedTextBlock).GetMethod("GetHoverOffset", Private).Invoke(c, null);
            Check(offset < 0, "RTL starts at the right end of text");
            panel.Children.Remove(c);
            await Until(() => !c.IsLoaded && Field(c, "_textFormat") == null, "unload cleanup");
            Check(Field(c, "_hoverTextLayout") == null && !(bool)Field(c, "_isClockRegistered"), "Unload stops all rendering");
            panel.Children.Add(c);
            await Until(() => c.IsLoaded && Field(c, "_staticTextLayout") != null, "reload renders again");
            Check(c.ActualHeight > 0, "Reload retains auto height");
            Hover(c);
            await Until(() => Field(c, "_hoverTextLayout") != null, "reload hover");
            c.TextWrapping = TextWrapping.Wrap;
            await Task.Delay(100);
            Check(Field(c, "_hoverTextLayout") == null, "Wrapped text does not use horizontal marquee");
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
