using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;

namespace FontUiRegression;
public sealed partial class FontSettingsControl : UserControl
{
    public FontPreferences Preferences { get; }
    public ComboBox ApplicationCombo => ApplicationFontComboBox;
    public ComboBox LyricsCombo => LyricsFontFamily;
    public ToggleSwitch Toggle => IndependentLyricsFontToggle;
    public ComboBox DesktopCombo => DesktopLyricsFontComboBox;
    public ToggleSwitch DesktopToggle => IndependentDesktopLyricsFontToggle;
    public FontSettingsControl(FontPreferences preferences)
    {
        Preferences = preferences;
        InitializeComponent();
    }
}
public sealed class FontPreferences : PreferencesStateAdapter
{
    public ObservableCollection<FontInfo> FontFamilyList { get; } = [];
}
// The production facade forwards these same child-state notifications.
public class PreferencesStateAdapter : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public PreferencesState State { get; } = new();
    public PreferencesStateAdapter() => State.PropertyChanged += (_, e) => OnPropertyChanged(e);
    public FontInfo ApplicationFontFamily { get => State.ApplicationFontFamily; set => State.ApplicationFontFamily = value; }
    public FontInfo FontFamily { get => State.FontFamily; set => State.FontFamily = value; }
    public FontInfo DesktopLyricsFontFamily { get => State.DesktopLyricsFontFamily; set => State.DesktopLyricsFontFamily = value; }
    public bool UseIndependentDesktopLyricsFont { get => State.UseIndependentDesktopLyricsFont; set => State.UseIndependentDesktopLyricsFont = value; }
    public bool UseIndependentLyricsFont { get => State.UseIndependentLyricsFont; set => State.UseIndependentLyricsFont = value; }
}
