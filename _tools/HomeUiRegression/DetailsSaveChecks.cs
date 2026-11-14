using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

internal sealed partial class RegressionApplication
{
    private async Task CheckDetailsSaveAsync(Window window, MusicDatabaseService data, AppViewModel app, FixtureEditorFactory factory)
    {
        var database = data.GetDbConnection();
        var songs = new[]
        {
            new Music { Title = "Save fixture", Author = "Artist", Album = "Album", Path = "save-a.flac", AdditionalMetadataRead = true },
            new Music { Title = "Save fixture", Author = "Artist", Album = "Album", Path = "save-b.flac", AdditionalMetadataRead = true }
        };
        foreach (var song in songs) await database.InsertAsync(song);
        await data.InitializeVersionsAsync();
        await data.Versions.ApplyAutomaticLinksAsync(data.Lyrics);
        songs = (await data.Versions.GetVersionsAsync(songs[0].Id)).ToArray();
        app.Replace(songs.ToList());
        UniversalOriginPage.Songs = songs;
        var navigation = new LibraryNavigationService();
        using var services = new ServiceCollection().AddSingleton(app).AddSingleton(data)
            .AddSingleton<IMusicDetailsEditorFactory>(factory).AddSingleton(factory.Sessions)
            .AddSingleton(navigation).BuildServiceProvider();
        App.Services = services;

        // Match the library's outer page / inner Frame ownership without opening the user's library.
        var frame = new Frame();
        window.Content = new Page { Content = frame };
        PolishNative.ShowWithoutActivation(window);
        int navigations = 0, closeRequests = 0;
        frame.Navigated += (_, _) => navigations++;
        using var presenter = new MusicDetailsPresenter(frame);
        navigation.Close = () => { closeRequests++; presenter.Back(); };
        frame.Navigate(typeof(UniversalOriginPage));
        await Wait(() => frame.Content is UniversalOriginPage { IsLoaded: true });
        var origin = (UniversalOriginPage)frame.Content;
        origin.List.SelectedIndex = 1;

        async Task<(MusicDetailsPanel Panel, MusicDetailControl Control, Button Save)> OpenAsync()
        {
            Check(presenter.Show(new MusicDetailsRequest(songs[0], typeof(UniversalOriginPage))), "open nested library details");
            var panel = presenter.CurrentDetails!;
            var control = (MusicDetailControl)panel.FindName("DetailView");
            var save = (Button)control.FindName("SaveDatabaseButton");
            await Wait(() => control.IsLoaded && panel.ViewModel is { Editor.IsBusy: false, Versions.Count: 2 }
                && save.IsEnabled && ((ContentControl)origin.FindName("MusicDetailsOverlay")).IsHitTestVisible);
            Check(ReferenceEquals(save.Command, panel.ViewModel!.Editor.SaveDatabaseCommand),
                "shipping compiled save binding resolves the active editor command");
            return (panel, control, save);
        }
        static void Click(Button button) => ((IInvokeProvider)FrameworkElementAutomationPeer
            .CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

        TaskCompletionSource? saveGate = null;
        try
        {
            var (panel, control, save) = await OpenAsync();
            var model = panel.ViewModel!;
            var editor = model.Editor;
            string originalTitle = songs[0].Title;
            ((TextBox)control.FindName("TitleInput")).Text = "Saved through nested library";
            editor.Lyrics.OriginalText = "[00:01.00]Clicked lyric snapshot";
            Check(editor.Metadata.Draft.Title == "Saved through nested library", "shipping title binding updates the private draft");
            saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.EditorSaveGate = saveGate.Task;
            Click(save);
            var saving = editor.SaveDatabaseCommand.ExecutionTask!;
            Check(closeRequests == 1 && !saving.IsCompleted && !editor.SaveDatabaseCommand.CanExecute(null),
                "click requests close synchronously and freezes the accepted save before any database work");
            await Wait(() => !presenter.IsOpen);
            Check((await database.GetAsync<Music>(songs[0].Id)).Title == originalTitle,
                "the source page returns while the database write is still blocked");
            var stopping = model.StopAsync();
            Check(!stopping.IsCompleted, "editor teardown retains the accepted write barrier");
            editor.Metadata.Draft.Title = "Late mutation";
            editor.Lyrics.OriginalText = "[00:02.00]Late lyric mutation";
            saveGate.SetResult();
            await Task.WhenAll(saving, stopping);
            Check((await database.GetAsync<Music>(songs[0].Id)).Title == "Saved through nested library"
                && (await data.Lyrics.GetAsync(songs[0].Id)).Document.Original.Content.Contains("Clicked lyric snapshot"),
                "metadata and lyrics persist the click snapshot even after the editor is closed or mutated");
            Check(ReferenceEquals(frame.Content, origin) && origin.List.SelectedIndex == 1 && navigations == 1 && closeRequests == 1,
                "completion preserves the source page and cannot request a second close");

            (panel, control, save) = await OpenAsync();
            model = panel.ViewModel!;
            ((TextBox)control.FindName("TitleInput")).Text = "Failed background save";
            data.FailEditorSave = true;
            int failures = factory.DatabaseSaveFailures.Count;
            Click(save);
            await model.Editor.SaveDatabaseCommand.ExecutionTask!;
            await Wait(() => !presenter.IsOpen);
            Check(factory.DatabaseSaveFailures.Count == failures + 1
                && factory.DatabaseSaveFailures[^1].Music.Id == songs[0].Id
                && factory.DatabaseSaveFailures[^1].Error.Message == "fixture metadata save failure"
                && (await database.GetAsync<Music>(songs[0].Id)).Title == "Saved through nested library",
                "background failure reaches the global notification boundary exactly once without changing stored tags");
            await model.StopAsync();
            data.FailEditorSave = false;

            (panel, control, save) = await OpenAsync();
            model = panel.ViewModel!;
            ((TextBox)control.FindName("TitleInput")).Text = "Retry after reopening";
            Click(save);
            await model.Editor.SaveDatabaseCommand.ExecutionTask!;
            await Wait(() => !presenter.IsOpen);
            Check((await database.GetAsync<Music>(songs[0].Id)).Title == "Retry after reopening",
                "reopening after a notified failure supports a fresh save");
            await model.StopAsync();

            (panel, control, save) = await OpenAsync();
            model = panel.ViewModel!;
            int before = closeRequests;
            Click(save);
            Check(closeRequests == before + 1, "unchanged drafts also close synchronously");
            await model.Editor.SaveDatabaseCommand.ExecutionTask!;
            await Wait(() => !presenter.IsOpen);
            await model.StopAsync();

            (panel, control, save) = await OpenAsync();
            model = panel.ViewModel!;
            model.SelectedVersion = model.Versions.Single(version => version.Music.Id == songs[1].Id);
            await Wait(() => !model.Editor.IsBusy && ReferenceEquals(save.Command, model.Editor.SaveDatabaseCommand));
            ((TextBox)control.FindName("TitleInput")).Text = "Saved second version";
            Click(save);
            await model.Editor.SaveDatabaseCommand.ExecutionTask!;
            await Wait(() => !presenter.IsOpen);
            Check((await database.GetAsync<Music>(songs[1].Id)).Title == "Saved second version",
                "a switched version retains its own compiled save command and database identity");
            await model.StopAsync();

            (panel, control, _) = await OpenAsync();
            model = panel.ViewModel!;
            await model.Editor.SaveFileCommand.ExecuteAsync(null);
            await Wait(() => !presenter.IsOpen);
            Check(await database.Table<PendingMetadataWrite>().CountAsync() == 1,
                "file-save retains its existing queue and close behavior");
            await model.StopAsync();

            (panel, control, save) = await OpenAsync();
            model = panel.ViewModel!;
            ((TextBox)control.FindName("TitleInput")).Text = "Save during close";
            saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.EditorSaveGate = saveGate.Task;
            Click(save);
            saving = model.Editor.SaveDatabaseCommand.ExecutionTask!;
            await Wait(() => !presenter.IsOpen);
            data.EditorSaveGate = Task.CompletedTask;
            var replacement = await OpenAsync();
            before = closeRequests;
            saveGate.SetResult();
            await Task.WhenAll(saving, model.StopAsync());
            Check(closeRequests == before && presenter.CurrentDetails == replacement.Panel,
                "an old editor's completion cannot dismiss a replacement panel");

            var replacementEditor = replacement.Panel.ViewModel!.Editor;
            ((TextBox)replacement.Control.FindName("TitleInput")).Text = "Accepted before exit";
            saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.EditorSaveGate = saveGate.Task;
            Click(replacement.Save);
            saving = replacementEditor.SaveDatabaseCommand.ExecutionTask!;
            app.State.Lifecycle.TryBeginExit(out _);
            var draining = factory.Tasks.DrainCommittedAsync();
            var sessionStop = factory.Sessions.StopAsync();
            Check(!draining.IsCompleted && !sessionStop.IsCompleted,
                "application shutdown drains already accepted writes instead of cancelling them");
            saveGate.SetResult();
            await Task.WhenAll(saving, draining, sessionStop);
            Check((await database.GetAsync<Music>(songs[0].Id)).Title == "Accepted before exit",
                "an accepted save commits after the application stopping token is cancelled");
        }
        finally
        {
            saveGate?.TrySetResult();
            data.EditorSaveGate = Task.CompletedTask;
            data.FailEditorSave = false;
            presenter.Dismiss();
            navigation.Close = null;
            await factory.Sessions.StopAsync();
            await factory.Tasks.DrainCommittedAsync();
            frame.Content = null;
        }
    }
}
