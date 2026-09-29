using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using LyricsUiRegression;
using SQLite;
internal static class Program
{
    [STAThread] static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new TestApplication();
        });
    }
}
internal sealed partial class TestApplication : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider[] _metadata =
    [new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider(),
     new CommunityToolkit.WinUI.Controls.SettingsControlsRns.CommunityToolkit_WinUI_Controls_SettingsControls_XamlTypeInfo.XamlMetaDataProvider()];
    public IXamlType GetXamlType(Type type) => _metadata.Select(provider => provider.GetXamlType(type)).FirstOrDefault(value => value is not null)!;
    public IXamlType GetXamlType(string name) => _metadata.Select(provider => provider.GetXamlType(name)).FirstOrDefault(value => value is not null)!;
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    public TestApplication()
    {
        UnhandledException += (_, e) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: " + e.Exception);
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var window = new Window { Title = "Lyrics isolated regression" };
        var lifecycle = new AppLifecycle();
        var tasks = new ApplicationTasks(lifecycle);
        string file = Path.Combine(AppContext.BaseDirectory, "result.txt");
        try
        {
            var db = new SQLiteAsyncConnection(Path.Combine(Path.GetTempPath(), "lyrics-ui-" + Guid.NewGuid().ToString("N") + ".db"));
            await db.CreateTableAsync<Music>(); await db.CreateTableAsync<MusicLyrics>();
            var music = new Music { Id = 1, Path = Path.Combine(Path.GetTempPath(), "lyrics-ui.flac") };
            await db.InsertAsync(music);
            var parser = new LyricsParser(); var database = new MusicDatabaseService(db, parser);
            await database.Lyrics.InitializeAsync(db.DatabasePath);
            var old = await database.Lyrics.GetAsync(1);
            await database.Lyrics.SaveAsync(1, parser.Import("[00:01.000]Original", "[00:01.000]翻译"), old.Revision, "User");
            var online = new LyricsOnlineSearch();
            var editor = new LyricsEditorViewModel(music, database, parser, online, tasks);
            var settings = new SettingsViewModel(new(), new());
            var panel = new TestPanel(editor, settings);
            window.Content = panel;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 650));
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.Activate();
            await editor.LoadAsync();
            await Task.Delay(200);
            var pivot = Descendants(panel).OfType<Pivot>().Single();
            var combo = Descendants(panel).OfType<ComboBox>().First();
            Check(pivot.Items.Count == 2, "two editor tabs");
            settings.State.Preferences.LocalLyricsFormatOrder = "ttml,lrc,krc,qrc";
            await Task.Delay(100);
            Check((combo.SelectedItem as SettingsViewModel.LyricsOrderOption)?.Value == "ttml,lrc,krc,qrc", "late settings restoration updates selection");
            foreach (var option in settings.LyricsOrderOptions)
            {
                combo.SelectedItem = option;
                Check(settings.State.Preferences.LocalLyricsFormatOrder == option.Value, "priority binding " + option.Value);
            }
            var pending = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
            online.Handler = (_, _, _) => pending.Task;
            var refresh = editor.RefreshCommand.ExecuteAsync(null);
            await Task.Delay(50);
            editor.OriginalText = "[00:01.000]Edited during download";
            pending.SetResult(parser.Import("[00:01.000]Downloaded"));
            await refresh;
            Check(editor.OriginalText.Contains("Edited") && editor.Error.Contains("Conflict"), "late search preserves dirty draft");
            var second = new LyricsEditorViewModel(music, database, parser, online, tasks);
            await second.LoadAsync();
            Check(await editor.SaveAsync(), "first editor commits");
            second.OriginalText = "[00:01.000]Second editor draft";
            Check(!await second.SaveAsync() && second.OriginalText.Contains("Second editor"), "conflicting editor keeps draft");
            second.Dispose();
            var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            database.BeforeSave = () => { saving.TrySetResult(); return continueSave.Task; };
            var save = editor.SaveAsync();
            await saving.Task;
            editor.OriginalText = "[00:01.000]Typed while SQLite commit was pending";
            continueSave.SetResult();
            Check(!await save && editor.OriginalText.Contains("Typed while"), "edits during save remain open as an unsaved draft");
            database.BeforeSave = null;
            Check(await editor.SaveAsync(), "retained draft saves against updated revision");
            await EditorRecoveryChecks.RunAsync(db, database, parser, tasks);
            File.WriteAllText(file, "PASS: real WinUI compiled binding, 2 tabs, 24 priorities, late restoration, stale network draft, SQLite edit conflict and edits during save; unknown-lyrics metadata save, raw legacy recovery, external clear/manual refresh and late-download clear persistence.\nREADY for UI Automation");
            if (Environment.GetCommandLineArgs().Contains("--uia")) await Task.Delay(TimeSpan.FromSeconds(90));
            editor.Dispose();
            lifecycle.TryBeginExit(out _);
            await tasks.DrainAsync();
            await db.CloseAsync();
        }
        catch (Exception ex) { File.WriteAllText(file, "FAIL: " + ex); }
        window.Close(); Exit();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i=0; i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
