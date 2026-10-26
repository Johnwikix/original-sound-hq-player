using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinUIMusicPlayer.Model;
using MusicMetadataUiRegression;

internal static class Program
{
    [STAThread] private static void Main()
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
    private readonly IXamlMetadataProvider _metadata = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _metadata.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private static string ResultPath => Path.Combine(AppContext.BaseDirectory, "result.txt");
    public TestApplication() => UnhandledException += (_, e) => File.WriteAllText(ResultPath, "FAIL: " + e.Exception);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var window = new Window { Title = "Music metadata isolated regression" };
        try
        {
            await LoadingRegression.RunAsync(window);
            var source = new Music { Id = 1, Title = "Original" };
            var panel = new MetadataPanel(source);
            window.Content = panel;
            window.AppWindow.MoveAndResize(new(-30000, -30000, 850, 700));
            window.Activate();
            await Task.Delay(150);
            var area = (Expander)panel.FindName("OptionalTags");
            Check(!area.IsExpanded && (string)area.Header == "其他音乐信息", "Absent tags are collapsed with localized header");
            area.IsExpanded = true;
            await Task.Delay(100);
            string[] names = ["AlbumArtist", "Genre", "Composer", "Lyricist", "Conductor", "CatalogNumber"];
            foreach (string name in names)
            {
                var input = (TextBox)panel.FindName(name + "Input");
                Check(input.Text == "" && input.PlaceholderText == "未提供（可选）" && input.ActualWidth > 100,
                    name + " empty display and layout");
                input.Text = name + " value";
            }
            await Task.Delay(100);
            var tags = AdditionalMusicMetadata.From(panel.MetadataEditor.Draft);
            Check(tags == new AdditionalMusicMetadata("AlbumArtist value", "Genre value", "Composer value", "Lyricist value", "Conductor value", "CatalogNumber value"),
                "All six shipping two-way bindings update the production model");
            Check(AdditionalMusicMetadata.From(source).IsEmpty, "Editing UI retains a private draft");
            var saved = panel.MetadataEditor.Capture();
            Check(panel.MetadataEditor.AcceptSaved(saved) && source.AlbumArtist == "AlbumArtist value", "Successful snapshot publishes tags");
            var remote = new MetadataPanel(new Music { SourceId = 1, Genre = "Jazz", AdditionalMetadataRead = true });
            window.Content = remote;
            await Task.Delay(100);
            Check(((Expander)remote.FindName("OptionalTags")).IsExpanded, "Existing tags expand automatically");
            foreach (string name in names) Check(((TextBox)remote.FindName(name + "Input")).IsReadOnly, "Remote tag is read-only: " + name);
            File.WriteAllText(ResultPath, "PASS: shipping loading/content bindings after real old-database backfill; loading/save/failure transitions.\nPASS: shipping optional-tag XAML compiled bindings; six editable fields; sparse default; localized labels/placeholders; layout; private draft; saved snapshot; populated expansion; remote read-only.");
        }
        catch (Exception ex) { File.WriteAllText(ResultPath, "FAIL: " + ex); }
        window.Close();
        Exit();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
