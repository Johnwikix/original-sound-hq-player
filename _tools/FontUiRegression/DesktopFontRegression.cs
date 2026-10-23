using System.Text.Json;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.State;

namespace FontUiRegression;

internal static class DesktopFontRegression
{
    public static void Run(FontPreferences preferences, FontSettingsControl settings, FontInfo segoe, FontInfo arial, FontInfo consolas, Action<bool, string> check)
    {
        var state = new AppState(preferences.State);
        using var shutdown = new ShutdownCoordinator();
        var vm = new DesktopLyricsViewModel(state, new MusicDatabaseService(), shutdown);
        preferences.DesktopLyricsFontFamily = arial;
        preferences.ApplicationFontFamily = segoe;
        vm.RestoreFromSettings();
        check(!settings.DesktopToggle.IsOn && !settings.DesktopCombo.IsEnabled, "Desktop font must default to linked/disabled.");
        check(ReferenceEquals(settings.DesktopCombo.SelectedItem, arial), "Disabled desktop selector lost its independent choice.");
        check(vm.Style.FontFamily == "Segoe UI", "Desktop startup restore did not use the application font.");
        int uiThread = Environment.CurrentManagedThreadId;
        bool notificationsOnUi = true;
        int changes = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.Style)) return;
            notificationsOnUi &= Environment.CurrentManagedThreadId == uiThread;
            changes++;
        };
        foreach (var mode in new[] { DesktopLyricsMode.Floating, DesktopLyricsMode.Taskbar })
        {
            vm.Mode = mode;
            preferences.DesktopLyricsFontFamily = arial;
            preferences.UseIndependentDesktopLyricsFont = false;
            preferences.ApplicationFontFamily = consolas;
            check(vm.Style.FontFamily == "Consolas", $"{mode}: linked desktop font did not update immediately.");
            settings.DesktopToggle.IsOn = true;
            check(preferences.UseIndependentDesktopLyricsFont && settings.DesktopCombo.IsEnabled && vm.Style.FontFamily == "Arial", $"{mode}: enabling the compiled desktop switch did not restore the independent font.");
            int before = changes;
            preferences.ApplicationFontFamily = segoe;
            check(vm.Style.FontFamily == "Arial" && changes == before, $"{mode}: global font changed independent desktop style.");
            settings.DesktopCombo.SelectedItem = consolas;
            check(preferences.DesktopLyricsFontFamily == consolas && vm.Style.FontFamily == "Consolas", $"{mode}: desktop selection did not update the production style snapshot.");
            settings.DesktopToggle.IsOn = false;
            check(!settings.DesktopCombo.IsEnabled && vm.Style.FontFamily == "Segoe UI" && preferences.DesktopLyricsFontFamily == consolas, $"{mode}: disabling did not keep the independent choice while linking to the program.");
            preferences.ApplicationFontFamily = arial;
            check(vm.Style.FontFamily == "Arial", $"{mode}: desktop lyrics stopped following program changes.");
            settings.DesktopToggle.IsOn = true;
            check(vm.Style.FontFamily == "Consolas", $"{mode}: re-enabling lost the desktop choice.");
            preferences.UseIndependentLyricsFont = true;
            preferences.FontFamily = segoe;
            check(vm.Style.FontFamily == "Consolas", $"{mode}: ordinary lyrics preferences leaked into desktop font.");
            preferences.DesktopLyricsFontFamily = null!;
            check(preferences.DesktopLyricsFontFamily == consolas, $"{mode}: empty selection erased the desktop font.");
            settings.DesktopToggle.IsOn = false;
        }
        check(notificationsOnUi && changes > 0, "Desktop font style notifications must remain on the UI thread.");
        var legacy = JsonSerializer.Deserialize("{\"DesktopLyricsFontFamily\":\"Arial\"}", SettingsJsonContext.Default.SaveSettings)!;
        check(!legacy.UseIndependentDesktopLyricsFont && legacy.DesktopLyricsFontFamily == "Arial", "Legacy desktop settings must default to linked while retaining their old font.");
        foreach (bool independent in new[] { false, true })
        {
            var saved = new SaveSettings { ApplicationFont = "Segoe UI", DesktopLyricsFontFamily = "Arial", UseIndependentDesktopLyricsFont = independent };
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(saved, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
            check(restored.UseIndependentDesktopLyricsFont == independent && restored.DesktopLyricsFontFamily == "Arial" && restored.ApplicationFont == "Segoe UI", "Desktop font/switch failed JSON persistence.");
        }
        var previous = vm.Style;
        shutdown.Dispose();
        preferences.ApplicationFontFamily = consolas;
        check(vm.Style == previous, "Desktop view model kept publishing style changes after cleanup.");
        preferences.UseIndependentLyricsFont = false;
    }
}
