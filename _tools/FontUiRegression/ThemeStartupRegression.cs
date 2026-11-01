using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace FontUiRegression;

/// <summary>使用 WinUIEx 实际的 WindowContent 包装层检查字体刷新是否覆盖窗口主题。</summary>
internal static class ThemeStartupRegression
{
    public static async Task RunAsync(ApplicationFontService service, FontInfo first, FontInfo second, Action<bool, string> check)
    {
        string previousTheme = AppSettings.AppTheme;
        string previousStyle = AppSettings.AppStyle;
        ElementTheme previousElementTheme = AppSettings.ElementTheme;
        try
        {
            foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Default })
            {
                foreach (string style in new[] { "Mica", "TransparentAcrylic" })
                {
                    foreach (bool attachFont in new[] { true, false })
                    {
                        await RunScenarioAsync(theme, style, attachFont, service, first, second, check);
                    }
                }
            }
        }
        finally
        {
            AppSettings.AppTheme = previousTheme;
            AppSettings.AppStyle = previousStyle;
            AppSettings.ElementTheme = previousElementTheme;
            service.Apply(first.FontFamily);
        }
    }

    private static async Task RunScenarioAsync(ElementTheme theme, string style, bool attachFont,
        ApplicationFontService service, FontInfo first, FontInfo second, Action<bool, string> check)
    {
        var saved = new SaveSettings { AppTheme = theme.ToString(), ApplicationFont = second.FontFamily.Source };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(saved, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
        AppSettings.AppTheme = restored.AppTheme;
        AppSettings.AppStyle = style;
        service.Apply(second.FontFamily);
        var inner = new Grid();
        var frame = new Frame();
        inner.Children.Add(frame);
        if (attachFont) ApplicationFont.SetIsEnabled(inner, true);
        // WindowEx 的 XAML ContentProperty 是 WindowContent；直接设置 Window.Content 会绕过包装层及主题同步回调。
        var window = new WinUIEx.WindowEx { WindowContent = inner };
        var outer = (FrameworkElement)window.Content;
        var helper = new ThemeStyleHelper(window, window.AppWindow);
        helper.SetAppStyle();
        helper.SetAppTheme();
        try
        {
            check(!ReferenceEquals(inner, outer) && outer.RequestedTheme == theme && inner.RequestedTheme == ElementTheme.Default,
                "Fixture must preserve the real WinUIEx outer theme and inherited inner theme before loading.");
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.Activate();
            await Task.Delay(60);
            check(inner.IsLoaded && outer.RequestedTheme == theme,
                $"Startup {theme}/{style}, font={attachFont}: Loaded/font registration overwrote the outer theme with {outer.RequestedTheme}; inner={inner.RequestedTheme}.");
            check(inner.ReadLocalValue(FrameworkElement.RequestedThemeProperty) == DependencyProperty.UnsetValue,
                "Font registration introduced a local theme on WindowContent.");
            var text = new TextBlock { Text = "Restored theme" };
            var page = new Page { Content = new NavigationView { Content = text } };
            frame.Content = page;
            await Task.Delay(50);
            ElementTheme expected = theme == ElementTheme.Default
                ? Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light
                : theme;
            check(outer.ActualTheme == expected && page.ActualTheme == expected && text.ActualTheme == expected,
                $"Startup {theme}/{style}, font={attachFont}: outer={outer.ActualTheme}, page={page.ActualTheme}, text={text.ActualTheme}.");
            var foreground = ((SolidColorBrush)text.Foreground).Color;
            check(expected == ElementTheme.Dark ? foreground.R > 200 : foreground.R < 80,
                $"Startup {theme}/{style}, font={attachFont}: foreground={foreground}, theme={text.ActualTheme}.");
            var configuration = window.SystemBackdrop.GetType().GetField("_backdropConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window.SystemBackdrop) as SystemBackdropConfiguration;
            if (configuration is not null)
                check(configuration.Theme.ToString() == expected.ToString(), $"Startup {theme}/{style}: backdrop={configuration.Theme}, expected={expected}.");
            service.Apply(first.FontFamily);
            await Task.Delay(30);
            check(outer.RequestedTheme == theme && text.ActualTheme == expected && inner.RequestedTheme == ElementTheme.Default,
                $"Live font refresh lost window theme {theme}/{style}, font={attachFont}.");
            if (attachFont)
                check(text.FontFamily.Source == first.FontFamily.Source, "Wrapped window text did not update with the application font.");
            var edited = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            AppSettings.AppTheme = edited.ToString();
            helper.SetAppTheme();
            service.Apply(second.FontFamily);
            await Task.Delay(30);
            check(outer.RequestedTheme == edited && text.ActualTheme == edited,
                "Font change after a settings theme edit overwrote the explicit window theme.");
            if (attachFont)
            {
                ApplicationFont.SetIsEnabled(inner, false);
                ApplicationFont.SetIsEnabled(inner, true);
                check(outer.RequestedTheme == edited && inner.ReadLocalValue(FrameworkElement.RequestedThemeProperty) == DependencyProperty.UnsetValue,
                    "Font reattachment changed the outer theme or introduced an inner local theme.");
            }
        }
        finally
        {
            ApplicationFont.SetIsEnabled(inner, false);
            window.Close();
        }
    }
}
