using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace FontUiRegression;

internal static class DialogFontRegression
{
    public static async Task RunAsync(Panel host, ApplicationFontService service, FontInfo first, FontInfo second, Action<bool, string> check)
    {
        ElementTheme previous = AppSettings.ElementTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Default })
            {
                AppSettings.ElementTheme = theme;
                service.Apply(first.FontFamily);
                var localStyle = new Style(typeof(TextBlock));
                localStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11d));
                var inherited = new TextBlock { Text = "Inherited custom-style text", Style = localStyle };
                var explicitText = new TextBlock { Text = "Explicit lyrics font", FontFamily = first.FontFamily };
                var icon = new FontIcon { Glyph = "\uE8D4" };
                string iconFont = icon.FontFamily.Source;
                var input = new TextBox { Text = "Keep this input", SelectionStart = 5, SelectionLength = 4 };
                var content = new StackPanel { Children = { new UserControl { Content = inherited }, explicitText, icon, input } };
                var dialog = new ContentDialog { Title = "Cached title", Content = content, CloseButtonText = "Close" };
                var opened = new TaskCompletionSource();
                dialog.Opened += (_, _) => opened.TrySetResult();
                TextBlock? firstTitle = null;
                var firstShow = dialog.ShowThemedAsync(host.XamlRoot);
                try
                {
                    await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(30);
                    var title = Find<TextBlock>(dialog, block => block.Text == "Cached title");
                    firstTitle = title;
                    check(title.FontFamily.Source == first.FontFamily.Source, $"{theme}: first title font did not match the application font.");
                    check(title.Visibility == Visibility.Visible && title.ActualHeight > 0 && title.Opacity > 0, "The asserted dialog title is not visible.");
                }
                finally
                {
                    dialog.Hide();
                    await firstShow;
                }
                service.Apply(second.FontFamily);
                await Task.Delay(30);
                check(((FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"]).Source == second.FontFamily.Source, "Global font resource did not change.");
                opened = new TaskCompletionSource();
                var secondShow = dialog.ShowThemedAsync(host.XamlRoot);
                try
                {
                    await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(30);
                    var title = Find<TextBlock>(dialog, block => block.Text == "Cached title");
                    check(title.FontFamily.Source == second.FontFamily.Source, $"{theme}: reopened cached title kept {title.FontFamily.Source}, expected {second.FontFamily.Source}.");
                    check(inherited.FontFamily.Source == second.FontFamily.Source, $"{theme}: custom-style content inherited the cached presenter font.");
                    check(explicitText.FontFamily.Source == first.FontFamily.Source && icon.FontFamily.Source == iconFont, "Dialog refresh overwrote an explicit font or an icon.");
                    check(ReferenceEquals(dialog.Content, content) && input.Text == "Keep this input", "Font update rebuilt or cleared dialog content.");
                    check(dialog.RequestedTheme == theme, "Font refresh changed the requested dialog theme.");
                    var close = Find<Button>(dialog, button => button.Content as string == "Close");
                    check(close.FontFamily.Source == second.FontFamily.Source, "Dialog button did not use the current application font.");
                    // Repeated show calls must keep the realized native template rather than rebuilding it.
                    check(ReferenceEquals(firstTitle, title), "Font refresh rebuilt the cached dialog title template.");
                }
                finally
                {
                    dialog.Hide();
                    await secondShow;
                }
                service.Apply(first.FontFamily);
                opened = new TaskCompletionSource();
                var thirdShow = dialog.ShowThemedAsync(host.XamlRoot);
                try
                {
                    await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(30);
                    check(Find<TextBlock>(dialog, block => block.Text == "Cached title").FontFamily.Source == first.FontFamily.Source,
                        "A second font change was not applied to the same cached dialog.");
                }
                finally
                {
                    dialog.Hide();
                    await thirdShow;
                }
            }
        }
        finally
        {
            AppSettings.ElementTheme = previous;
            service.Apply(first.FontFamily);
        }
    }

    private static T Find<T>(DependencyObject node, Func<T, bool> match) where T : DependencyObject =>
        TryFind(node, match) ?? throw new Exception($"Missing visible dialog {typeof(T).Name}.");

    private static T? TryFind<T>(DependencyObject node, Func<T, bool> match) where T : DependencyObject
    {
        if (node is T value && match(value)) return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            if (TryFind(VisualTreeHelper.GetChild(node, index), match) is { } child) return child;
        return null;
    }
}
