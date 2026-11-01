using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WinUIMusicPlayer.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using FontUiRegression;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

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
    private int _checks;
    private readonly IXamlMetadataProvider[] _metadata =
    [
        new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider(),
        new CommunityToolkit.WinUI.Controls.SettingsControlsRns.CommunityToolkit_WinUI_Controls_SettingsControls_XamlTypeInfo.XamlMetaDataProvider(),
        (IXamlMetadataProvider)Activator.CreateInstance(typeof(FontSettingsControl).Assembly.GetType(
            "FontUiRegression.FontUiRegression_XamlTypeInfo.XamlMetaDataProvider", throwOnError: true)!)!
    ];
    public IXamlType GetXamlType(Type type) => _metadata.Select(p => p.GetXamlType(type)).FirstOrDefault(t => t is not null)!;
    public IXamlType GetXamlType(string name) => _metadata.Select(p => p.GetXamlType(name)).FirstOrDefault(t => t is not null)!;
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    public RegressionApplication() => UnhandledException += (_, args) =>
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: " + args.Exception);
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window? window = null;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///CustomFontStyle.xaml") });
            using var service = new ApplicationFontService();
            using var provider = new ServiceCollection().AddSingleton(service).BuildServiceProvider();
            WinUIMusicPlayer.App.Services = provider;
            var preferences = new FontPreferences();
            var segoe = FontInfo.CreateDefault();
            var arial = new FontInfo { Name = "Arial", DisplayName = "Arial", FontFamily = new FontFamily("Arial") };
            var consolas = new FontInfo { Name = "Consolas", DisplayName = "Consolas", FontFamily = new FontFamily("Consolas") };
            preferences.FontFamilyList.Add(segoe);
            preferences.FontFamilyList.Add(arial);
            preferences.FontFamilyList.Add(consolas);
            preferences.ApplicationFontFamily = segoe;
            preferences.FontFamily = consolas;
            service.Apply(segoe.FontFamily);
            preferences.State.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(preferences.ApplicationFontFamily)) service.Apply(preferences.ApplicationFontFamily.FontFamily);
            };
            var settings = new FontSettingsControl(preferences);
            var text = new TextBlock { Text = "Global text" };
            var styled = new TextBlock { Text = "Styled text", Style = (Style)Resources["BodyStrongTextBlockStyle"] };
            var button = new Button { Content = "Button" };
            var input = new TextBox { Text = "Input" };
            var rich = new RichTextBlock();
            rich.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "Rich text" } } });
            var icon = new FontIcon { Glyph = "\uE8D4" };
            var preview = new TextBlock { Text = "Explicit font", FontFamily = consolas.FontFamily };
            var host = new StackPanel { Children = { settings, text, styled, button, input, rich, icon, preview } };
            ApplicationFont.SetIsEnabled(host, true);
            window = new Window { Content = host };
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.Activate();
            await Task.Delay(100);
            await ThemeStartupRegression.RunAsync(service, segoe, arial, Check);
            string iconFont = icon.FontFamily.Source;
            Check(!settings.Toggle.IsOn && !settings.LyricsCombo.IsEnabled, "Default lyrics selector must be disabled.");
            Check(ReferenceEquals(settings.LyricsCombo.SelectedItem, consolas), "Disabled selection lost the saved lyrics font.");
            Check(preferences.State.EffectiveLyricsFontFamilyName == "Segoe UI", "Default lyrics font must follow the application.");
            Check(text.FontFamily.Source == "Segoe UI" && styled.FontFamily.Source == "Segoe UI" && button.FontFamily.Source == "Segoe UI", "Initial global font did not reach plain/styled/control text.");
            foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Default })
            {
                preferences.FontFamily = consolas;
                host.RequestedTheme = theme;
                settings.ApplicationCombo.SelectedItem = arial;
                await Task.Delay(30);
                Check(preferences.ApplicationFontFamily == arial, "Application selection did not update the shared state.");
                Check(text.FontFamily.Source == "Arial" && styled.FontFamily.Source == "Arial" && button.FontFamily.Source == "Arial" && input.FontFamily.Source == "Arial" && rich.FontFamily.Source == "Arial", "Live font update missed a text/control type.");
                Check(host.RequestedTheme == theme, "Font refresh changed the selected theme.");
                Check(icon.FontFamily.Source == iconFont && preview.FontFamily.Source == "Consolas", "Global font overwrote an icon or explicit font.");
                Check(preferences.State.EffectiveLyricsFontFamilyName == "Arial", "Linked lyrics did not follow a live application change.");
                settings.Toggle.IsOn = true;
                Check(preferences.UseIndependentLyricsFont && settings.LyricsCombo.IsEnabled, "Switch failed to enable the real compiled selector binding.");
                Check(preferences.State.EffectiveLyricsFontFamilyName == "Consolas", "Independent lyrics did not use their saved font.");
                settings.LyricsCombo.SelectedItem = segoe;
                settings.Toggle.IsOn = false;
                settings.ApplicationCombo.SelectedItem = consolas;
                Check(preferences.FontFamily == segoe && preferences.State.EffectiveLyricsFontFamilyName == "Consolas", "Disabling lyrics font discarded the independent selection or ignored the program font.");
                settings.Toggle.IsOn = true;
                Check(preferences.State.EffectiveLyricsFontFamilyName == "Segoe UI", "Re-enabling failed to restore the saved independent font.");
                settings.Toggle.IsOn = false;
            }
            var newButton = new Button { Content = "Created after change" };
            host.Children.Add(newButton);
            await Task.Delay(20);
            Check(newButton.FontFamily.Source == "Consolas", "Newly created controls ignored the current font.");
            preferences.ApplicationFontFamily = null!;
            preferences.FontFamily = null!;
            Check(preferences.ApplicationFontFamily == consolas && preferences.FontFamily == segoe, "Transient empty ComboBox selection erased font state.");
            var legacy = JsonSerializer.Deserialize("{\"GlobalFont\":\"Arial\"}", SettingsJsonContext.Default.SaveSettings)!;
            Check(legacy.ApplicationFont == "Segoe UI" && !legacy.UseIndependentLyricsFont && legacy.GlobalFont == "Arial", "Legacy settings did not retain lyrics selection and new defaults.");
            var saved = new SaveSettings { ApplicationFont = "Consolas", GlobalFont = "Arial", UseIndependentLyricsFont = true };
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(saved, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
            Check(restored.ApplicationFont == saved.ApplicationFont && restored.GlobalFont == saved.GlobalFont && restored.UseIndependentLyricsFont, "Font settings did not survive source-generated JSON serialization.");
            DesktopFontRegression.Run(preferences, settings, segoe, arial, consolas, Check);
            await DialogFontRegression.RunAsync(host, service, consolas, arial, Check);
            ApplicationFont.SetIsEnabled(host, false);
            int themeChanges = 0;
            host.ActualThemeChanged += (_, _) => themeChanges++;
            service.Apply(arial.FontFamily);
            Check(themeChanges == 0, "Disabling the root attachment retained resource tracking.");
            ApplicationFont.SetIsEnabled(host, true);
            await Task.Delay(30);
            Check(text.FontFamily.Source == "Arial", $"Reattaching: loaded={host.IsLoaded}, text={text.FontFamily.Source}, button={button.FontFamily.Source}, theme={host.RequestedTheme}/{host.ActualTheme}, changes={themeChanges}, resource={((FontFamily)Resources["ContentControlThemeFontFamily"]).Source}.");
            service.Apply(consolas.FontFamily);
            var secondaryHost = new StackPanel { Children = { new TextBlock { Text = "Secondary window" } } };
            ApplicationFont.SetIsEnabled(secondaryHost, true);
            var secondaryWindow = new Window { Content = secondaryHost };
            secondaryWindow.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            secondaryWindow.Activate();
            await Task.Delay(30);
            service.Apply(arial.FontFamily);
            Check(((TextBlock)secondaryHost.Children[0]).FontFamily.Source == "Arial", "Secondary window did not track the application font.");
            int secondaryThemeChanges = 0;
            secondaryHost.ActualThemeChanged += (_, _) => secondaryThemeChanges++;
            secondaryWindow.Close();
            await Task.Delay(30);
            secondaryThemeChanges = 0;
            service.Apply(consolas.FontFamily);
            Check(secondaryThemeChanges == 0, "Closed secondary window remained registered for font refresh.");
            service.Dispose();
            service.Apply(segoe.FontFamily);
            Check(((FontFamily)Resources["ContentControlThemeFontFamily"]).Source == "Consolas", "Disposed service accepted a late font change.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), $"PASS: {_checks} checks; real WinUIEx WindowContent wrapper preserves Light/Dark/Default on font registration, live changes and reattachment with Mica/Acrylic; new page theme/foreground, compiled font bindings, icon/explicit-font preservation, independent ordinary/desktop lyrics, cleanup, serialization and cached ContentDialog fonts.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: " + ex);
        }
        finally { window?.Close(); Exit(); }
    }
}
