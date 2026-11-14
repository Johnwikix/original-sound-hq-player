using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class RegressionApplication
{
    private async Task CheckPlaylistRowAsync(Window window)
    {
        var music = new Music { Id = 31, Title = "Playlist binding fixture" };
        var first = new PlayListMusicItem { EntryId = 101, Music = music };
        var duplicate = new PlayListMusicItem { EntryId = 102, Music = music, RequestedMusicId = music.Id };
        var row = new MusicListRowControl { Width = 1000, Music = music };
        window.Content = row;
        PolishNative.ShowWithoutActivation(window);
        var button = (Button)row.FindName("CoverThumbnail");
        await Wait(() => row.IsLoaded && ReferenceEquals(button.CommandParameter, music));
        Check(ReferenceEquals(button.Command, music.PlayCommand), "ordinary row uses the existing Music command and parameter");

        // Match PlaylistDetailControl's generated Update_: Music, command, then entry.
        row.RowPlayCommand = first.PlayCommand;
        row.RowPlayParameter = first;
        await Wait(() => ReferenceEquals(button.CommandParameter, first));
        Check(ReferenceEquals(button.Command, first.PlayCommand), "playlist initialization never queries a typed command with a Music parameter");
        Invoke(button);
        await Wait(() => ReferenceEquals(MusicCommands.LastPlaylistEntry, first));
        Check(true, "cover button invokes the existing playlist entry command");

        row.RowPlayParameter = duplicate;
        Invoke(button);
        await Wait(() => ReferenceEquals(MusicCommands.LastPlaylistEntry, duplicate));
        Check(MusicCommands.LastPlaylistEntry is { EntryId: 102 } played && played.RequestedMusicId == music.Id,
            "recycled duplicate song keeps the selected relation identity and fixed version");

        row.RowPlayCommand = null;
        Check(ReferenceEquals(button.Command, music.PlayCommand) && ReferenceEquals(button.CommandParameter, music),
            "clearing the custom command restores a matching Music pair before its old entry parameter is cleared");
        row.RowPlayParameter = null;
        Invoke(button);
        await Wait(() => ReferenceEquals(MusicCommands.LastRowMusic, music));

        row.RowPlayParameter = first;
        Check(ReferenceEquals(button.CommandParameter, music), "entry-first initialization keeps the ordinary command paired with Music");
        row.RowPlayCommand = first.PlayCommand;
        Check(ReferenceEquals(button.CommandParameter, first), "entry-first initialization installs the playlist pair");
        row.RowPlayParameter = null;
        Check(button.Command is null, "incomplete playlist input detaches its typed command");
        row.RowPlayCommand = null;
        row.Music = null;
        Check(button.Command is null && button.CommandParameter is null, "empty recycled row clears both command and parameter");

        var blocked = new RelayCommand<PlayListMusicItem>(_ => throw new Exception("disabled command ran"), _ => false);
        row.RowPlayCommand = blocked;
        row.RowPlayParameter = first;
        Check(!button.IsEnabled, "the existing command still controls button availability");

        row.RowPlayCommand = first.PlayCommand;
        row.Music = music;
        window.Content = new Grid();
        await Wait(() => !row.IsLoaded);
        window.Content = row;
        await Wait(() => row.IsLoaded);
        Invoke(button);
        await Wait(() => ReferenceEquals(MusicCommands.LastPlaylistEntry, first));
        Check(button.IsEnabled, "unload and reload preserve the playlist command without extra subscriptions");
    }

    private static void Invoke(Button button)
        => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
}
