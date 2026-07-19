using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class UpdateHistoryDialog : ContentDialog
{
    public UpdateHistoryDialog(string version, string releaseNotes, string githubUrl)
    {
        InitializeComponent();
        Title = $"{ToolUtils.GetString("AppMainTitle")} v{version}";
        ReleaseNotesText.Text = releaseNotes;
        GitHubLink.Content = "⭐ Star on GitHub";
        GitHubLink.NavigateUri = new System.Uri(githubUrl);
        CloseButtonText = ToolUtils.GetString("DialogClose");
    }
}
