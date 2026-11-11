using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

internal sealed partial class RegressionApplication
{
    private async Task CheckSongVersionsAsync(Window window, MusicDatabaseService data, AppViewModel app, FixtureEditorFactory factory)
    {
        var database = data.GetDbConnection();
        var first = new Music { Title = "Version fixture", Author = "Artist", Album = "Album", Extension = "flac", CodecId = "flac", EncodingKind = 1,
            Path = "fixture-a.flac", Genre = "Jazz", Year = 2001, AdditionalMetadataRead = true, SampleRate = 44100, BitDepth = 16 };
        var second = new Music { Title = first.Title, Author = first.Author, Album = first.Album, Extension = "mp3", CodecId = "mp3", EncodingKind = 2,
            Path = "fixture-b.mp3", Genre = "Rock", Year = 2002, AdditionalMetadataRead = true, BitRate = 320, SampleRate = 44100 };
        await database.InsertAsync(first);
        await database.InsertAsync(second);
        await data.InitializeVersionsAsync();
        await data.Versions.ApplyAutomaticLinksAsync(data.Lyrics);
        Check((await data.Versions.GetVersionsAsync(first.Id)).Count == 2, "existing duplicates are linked automatically before opening details");
        var versions = await data.Versions.GetVersionsAsync(first.Id);
        app.Replace(versions);
        first = versions[0];
        second = versions[1];
        app.CurrentPlayingMusic = first;
        app.IsPlaybackEngineReady = true;
        using var services = new ServiceCollection().AddSingleton(app).AddSingleton(data)
            .AddSingleton<IMusicDetailsEditorFactory>(factory).AddSingleton(factory.Sessions)
            .AddSingleton(new LibraryNavigationService()).BuildServiceProvider();
        App.Services = services;
        var model = new MusicDetailsViewModel(factory.Create(first), app, data, factory.Sessions,
            NullLogger.Instance, MusicCommands.DetailsPlayCommand, new MusicDetailsRequest(first, null), factory.CreateSpectrogram(first),
            factory: factory, dialogs: new MusicVersionDialogs());
        var control = new MusicDetailControl { ViewModel = model, RequestedTheme = ElementTheme.Light,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
        var host = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Children = { control } };
        window.Content = host;
        PolishNative.ShowWithoutActivation(window);
        model.Start();
        try
        {
            await Wait(() => control.IsLoaded && model.Versions.Count == 2 && !model.Editor.IsBusy);
            var pivot = (Pivot)control.FindName("WorkspaceTabs");
            Check(pivot.SelectedIndex == 0 && ReferenceEquals(pivot.Items[0], control.FindName("VersionsTab")),
                "shipping details opens with the versions Pivot first");
            Check(Descendants<TextBlock>(control).Any(t => t.Text == WinUIMusicPlayer.Utils.ToolUtils.GetString("SongVersionsSetPreferred")),
                "compiled string ContentTemplate resolves version action labels");
            Check(model.Music.Genre == "Jazz" && model.Music.Year == 2001, "first version owns its optional info");
            var list = Descendants<ListView>(pivot).First(view => ReferenceEquals(view.ItemsSource, model.Versions));
            model.Editor.Metadata.Draft.Genre = "Unsaved first";
            model.Editor.Lyrics.OriginalText = "[00:01.00]Shared draft";
            list.SelectedIndex = 1;
            await Wait(() => model.Music.Id == second.Id && !model.Editor.IsBusy);
            Check(model.Editor.Metadata.Draft.Genre == "Rock" && model.Editor.Metadata.Draft.Year == 2002,
                "real ListView selection switches version-specific info through compiled binding");
            Check(model.Editor.Lyrics.OriginalText.Contains("Shared draft"), "shared lyric draft survives version switch");
            model.Editor.Metadata.Draft.Genre = "Unsaved second";
            list.SelectedIndex = 0;
            await Wait(() => model.Music.Id == first.Id);
            Check(model.Editor.Metadata.Draft.Genre == "Unsaved first", "first version tag draft survives round trip");
            Check(!model.SplitVersionCommand.CanExecute(null), "structural actions protect unsaved drafts");
            model.Editor.Metadata.Draft.Genre = "Jazz";
            await model.Editor.Lyrics.SaveAsync(null, false);
            list.SelectedIndex = 1;
            await Wait(() => model.Music.Id == second.Id);
            Check(model.Editor.Metadata.Draft.Genre == "Unsaved second", "second version draft remains independent");
            model.Editor.Metadata.Draft.Genre = "Rock";
            Check((await data.Lyrics.GetAsync(second.Id)).Document.Original.Content.Contains("Shared draft"),
                "saving shared lyrics is visible through either concrete version");
            Check(app.CurrentPlayingMusic == first && app.CurrentPlayingMusic.Id == first.Id,
                "view selection leaves actual playback unchanged");
            await model.SetPreferredCommand.ExecuteAsync(null);
            Check((await data.Versions.GetSongAsync(second.Id))!.PreferredMusicId == second.Id && app.CurrentPlayingMusic == first,
                "manual preference persists without changing current playback");
            var request = (MusicDetailsRequest)model.PlayParameter!;
            Check(request.ExactVersion && request.Music.Id == second.Id, "details play binds an explicit version request");
            await model.UseAutomaticCommand.ExecuteAsync(null);
            Check((await data.Versions.GetSongAsync(second.Id))!.PreferredMusicId is null, "automatic preference can be restored");
            var dialog = new MusicVersionDialogControl(new MusicVersionDialogViewModel(versions, true, "Preview") { TargetLyrics = "A", OtherLyrics = "B" });
            window.Content = dialog;
            await Wait(() => dialog.IsLoaded);
            Check(Descendants<ComboBox>(dialog).Single().SelectedIndex == 0 && Descendants<CheckBox>(dialog).Single().IsChecked == false,
                "merge preview defaults to existing lyrics and retains duplicate playlist positions");
            window.Content = host;
            await Wait(() => control.IsLoaded);
            await Task.Delay(100);
            await Capture(host, "song-versions-desktop.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(740, 760));
            await Task.Delay(150);
            Check(control.ActualWidth > 0 && list.ActualHeight >= 100 && Descendants<Button>(control).Where(button => button.Command == model.SetPreferredCommand).All(button => button.ActualWidth > 0),
                "versions actions remain realized in the narrow shipping layout");
            await Capture(host, "song-versions-narrow.png");
            await model.SplitVersionCommand.ExecuteAsync(null);
            Check((await data.Versions.GetVersionsAsync(second.Id)).Count == 1, "split command commits and refreshes selected group");
            await model.UndoVersionCommand.ExecuteAsync(null);
            Check(model.Versions.Count == 2, "split recovery reconnects existing editor sessions");
            var third = new Music { Title = first.Title, Author = first.Author, Album = first.Album, Path = "fixture-c.ogg", Extension = "ogg", AdditionalMetadataRead = true };
            await database.InsertAsync(third);
            await database.RunInTransactionAsync(db => SongVersionStore.AttachInTransaction(db, third));
            var thirdLyrics = await data.Lyrics.GetAsync(third.Id);
            await data.Lyrics.SaveAsync(third.Id, new(new("[00:02.00]Other timeline", LyricsFormat.Lrc), null), thirdLyrics.Revision, "User", expectedOwnerId: thirdLyrics.OwnerId);
            var conflict = await data.Versions.PreviewMergeAsync(second.Id, third.Id, data.Lyrics);
            await data.Versions.MergeAsync(conflict, conflict.OtherSongId, false);
            await app.RefreshSongsSourceAsync();
            await Wait(() => model.Versions.Count == 3 && model.Editor.Lyrics.DocumentOwnerId == -second.Id);
            list.SelectedIndex = 0;
            await Wait(() => model.Music.Id == first.Id && model.Editor.Lyrics.DocumentOwnerId == -first.Id);
            model.Editor.Lyrics.OriginalText = "[00:01.00]Independent override draft";
            list.SelectedIndex = 1;
            await Wait(() => model.Music.Id == second.Id);
            Check(!model.Editor.Lyrics.OriginalText.Contains("Independent override"), "merge ownership changes detach per-version override drafts");
        }
        finally
        {
            control.ViewModel = null;
            await model.StopAsync();
            Task drained = factory.Sessions.StopAsync();
            Check(drained.IsCompleted, "closing details drains all visited version sessions");
            await drained;
        }
    }
}
