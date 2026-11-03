using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CompositionColorBrush = Windows.UI.Composition.CompositionColorBrush;
using WinRT;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

namespace FontUiRegression;

/// <summary>在真实 WinUI 窗口中模拟云母不受支持，检查不透明回退及其生命周期。</summary>
internal static class MicaFallbackRegression
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        AppSettings.AppStyle = "Mica";
        AppSettings.AppTheme = "Dark";
        var window = new WinUIEx.WindowEx { WindowContent = new Grid() };
        var root = (FrameworkElement)window.Content;
        var helper = new ThemeStyleHelper(window, window.AppWindow);
        var target = window.As<ICompositionSupportsSystemBackdrop>();
        bool closed = false;
        try
        {
            // 只替换原生能力检测的结果；连接、主题事件及合成画刷均使用生产实现。
            helper.SetAppStyle(false);
            helper.SetAppTheme();
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.AppWindow.Show(false);
            await Task.Delay(60);
            check(window.SystemBackdrop is SolidColorSystemBackdrop, "Unsupported Mica did not select the solid fallback.");
            var fallback = window.SystemBackdrop;
            var brush = target.SystemBackdrop as CompositionColorBrush;
            check(brush is not null, "Solid fallback did not attach a real composition color brush.");

            foreach (string theme in new[] { "Light", "Dark", "Default" })
            {
                AppSettings.AppTheme = theme;
                helper.SetAppTheme();
                await Task.Delay(30);
                CheckColor(brush!, root.ActualTheme, check);
                helper.SetAppStyle(false);
                check(ReferenceEquals(fallback, window.SystemBackdrop) && ReferenceEquals(brush, target.SystemBackdrop),
                    "Reapplying unsupported Mica rebuilt the fallback or its brush.");
            }

            // 直接触发继承主题变化，覆盖跟随系统时不再调用 SetAppTheme 的通知路径。
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            await Task.Delay(30);
            CheckColor(brush!, root.ActualTheme, check);
            check(AppSettings.AppStyle == "Mica", "Fallback overwrote the saved Mica choice.");

            AppSettings.AppStyle = "TransparentTint";
            helper.SetAppStyle(false);
            await Task.Delay(30);
            check(window.SystemBackdrop is WinUIEx.TransparentTintBackdrop { TintColor.A: 0 },
                "Explicit transparent style failed after the solid fallback.");
            check(GetRoot(fallback!) is null, "Disconnected fallback retained its theme subscription root.");

            AppSettings.AppStyle = "Mica";
            helper.SetAppStyle(false);
            await Task.Delay(30);
            check(ReferenceEquals(fallback, window.SystemBackdrop), "Returning to Mica did not reuse its fallback.");
            CheckColor((CompositionColorBrush)target.SystemBackdrop, root.ActualTheme, check);
            if (CustomMicaSystemBackdrop.IsSupported())
            {
                helper.SetAppStyle();
                check(window.SystemBackdrop is CustomMicaSystemBackdrop, "Supported Mica stopped using its native controller.");
                check(GetRoot(fallback!) is null, "Switching to native Mica retained the fallback subscription.");
                helper.SetAppStyle(false);
                await Task.Delay(30);
            }
            window.Close();
            closed = true;
            check(GetRoot(fallback!) is null, "Closed fallback window retained its theme subscription root.");
        }
        finally
        {
            if (!closed) window.Close();
        }
    }

    private static void CheckColor(CompositionColorBrush brush, ElementTheme theme, Action<bool, string> check)
    {
        var color = brush.Color;
        byte expected = theme == ElementTheme.Dark ? (byte)32 : (byte)255;
        check(color.A == 255 && color.R == expected && color.G == expected && color.B == expected,
            $"Mica fallback must be opaque and match {theme}; actual={color}.");
    }

    private static object? GetRoot(object backdrop) => typeof(SolidColorSystemBackdrop)
        .GetField("_rootElement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(backdrop);
}
