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
            var formatCombos = new[] { "FirstLyricsFormat", "SecondLyricsFormat", "ThirdLyricsFormat", "FourthLyricsFormat" }
                .Select(name => (ComboBox)panel.FindName(name)).ToArray();
            var sourceCombos = new[] { "FirstLyricsSource", "SecondLyricsSource" }
                .Select(name => (ComboBox)panel.FindName(name)).ToArray();
            Check(pivot.Items.Count == 2, "two editor tabs");
            settings.State.Preferences.LocalLyricsFormatOrder = "ttml,lrc,krc,qrc";
            await Task.Delay(100);
            Check(formatCombos.Select(combo => combo.SelectedIndex).SequenceEqual(new[] { 3, 2, 0, 1 }), "late settings restoration updates all four selections");
            string[] formats = ["krc", "qrc", "lrc", "ttml"];
            int permutations = 0;
            foreach (int first in Enumerable.Range(0, 4))
            foreach (int secondFormat in Enumerable.Range(0, 4))
            foreach (int third in Enumerable.Range(0, 4))
            foreach (int fourth in Enumerable.Range(0, 4))
            {
                int[] order = [first, secondFormat, third, fourth];
                if (order.Distinct().Count() != 4) continue;
                settings.State.Preferences.LocalLyricsFormatOrder = string.Join(',', order.Select(index => formats[index]));
                Check(formatCombos.Select(combo => combo.SelectedIndex).SequenceEqual(order), "format permutation binding");
                permutations++;
            }
            Check(permutations == 24, "all 24 permutations restore without duplicate selections");
            for (int priority = 0; priority < 4; priority++)
            for (int selected = 0; selected < 4; selected++)
            {
                settings.State.Preferences.LocalLyricsFormatOrder = "krc,qrc,lrc,ttml";
                string[] expected = (string[])formats.Clone();
                (expected[priority], expected[selected]) = (expected[selected], expected[priority]);
                formatCombos[priority].SelectedIndex = selected;
                Check(settings.State.Preferences.LocalLyricsFormatOrder == string.Join(',', expected), "user selection swaps occupied format");
                Check(formatCombos.Select(combo => formats[combo.SelectedIndex]).SequenceEqual(expected), "compiled two-way binding updates swapped peer");
            }
            settings.FirstLyricsFormatIndex = -1;
            settings.SecondLyricsFormatIndex = 4;
            Check(formatCombos.All(combo => combo.SelectedIndex >= 0), "transient invalid selections do not change stored order");
            settings.State.Preferences.PreferDatabaseLyrics = true;
            Check(sourceCombos[0].SelectedIndex == 1 && sourceCombos[1].SelectedIndex == 0, "late source restoration updates both selections");
            for (int priority = 0; priority < 2; priority++)
            for (int selected = 0; selected < 2; selected++)
            {
                sourceCombos[priority].SelectedIndex = selected;
                Check(sourceCombos[0].SelectedIndex != sourceCombos[1].SelectedIndex &&
                    settings.State.Preferences.PreferDatabaseLyrics == (sourceCombos[0].SelectedIndex == 1), "source selection swaps peer and commits preference");
            }
            settings.FirstLyricsSourceIndex = -1;
            settings.SecondLyricsSourceIndex = 2;
            Check(sourceCombos[0].SelectedIndex != sourceCombos[1].SelectedIndex, "invalid source indices are ignored");
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
            File.WriteAllText(file, "PASS: real WinUI compiled binding, 2 tabs, 4 format/2 source dropdowns, 24 permutations, 16 format swaps, source swaps, invalid selection guards, late restoration, stale network draft, SQLite edit conflict and edits during save; unknown-lyrics metadata save, raw legacy recovery, external clear/manual refresh and late-download clear persistence.\nREADY for UI Automation");
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
