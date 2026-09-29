using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;
namespace LyricsUiRegression;
public sealed partial class TestPanel : UserControl
{
    public LyricsEditorViewModel LyricsEditor { get; }
    public SettingsViewModel ViewModel { get; }
    public TestPanel(LyricsEditorViewModel editor, SettingsViewModel settings)
    {
        LyricsEditor = editor; ViewModel = settings; InitializeComponent();
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        Status.Text = await LyricsEditor.SaveAsync() ? "Saved" : "Not saved";
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-action.txt"), Status.Text + "\n" + LyricsEditor.OriginalText);
    }
}
