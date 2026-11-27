using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SQLite;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;

namespace WebDavTreeUiRegression;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new TestApp();
        });
    }
}

public sealed partial class TestApp : Application
{
    private Window? _window;
    private static readonly string ResultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");

    public TestApp()
    {
        UnhandledException += (_, args) =>
        {
            File.WriteAllText(ResultPath, "UNHANDLED: " + args.Exception);
            Environment.Exit(1);
        };
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(ResultPath, "STARTED");
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--auto-links"))
            {
                await CheckAutomaticRemoteAsync();
                File.WriteAllText(ResultPath, "PASS: 6 production SQLite remote version checks; no HTTP request or user library access.");
                return;
            }
            var host = new Grid();
            _window = new Window { Content = host, Title = "WebDAV TreeView UI regression" };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 1000));
            _window.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
            _window.Activate();
            await UntilAsync(() => host.XamlRoot is not null, "window root");

            if (Environment.GetCommandLineArgs().Contains("--slow-save"))
            {
                await CheckSlowSaveAsync(host.XamlRoot);
                File.WriteAllText(ResultPath, "PASS: native Primary closes before durable save, progress is visible, failure strips secrets, retry works and shutdown drains the accepted save.");
                return;
            }

            var browser = new WebDavBrowserDialog { XamlRoot = host.XamlRoot };
            await CheckDialogAsync(browser, browser.TestTree, "browser");
            await CheckConnectionAsync(host.XamlRoot);
            await CheckCatalogConnectionAsync(host.XamlRoot, RemoteSourceKind.Jellyfin);
            await CheckCatalogConnectionAsync(host.XamlRoot, RemoteSourceKind.Navidrome);
            await CheckLibraryAndSourcesAsync(host);
            await CheckSlowSaveAsync(host.XamlRoot);
            File.WriteAllText(ResultPath, "PASS: real WebDAV tree and catalog connection dialogs; native Primary closes before a slow save; safe failure, retry and shutdown; production SQLite scope and source selection.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(ResultPath, "FAIL: " + ex);
        }
        finally
        {
            _window?.Close();
            Exit();
        }
    }

    private static async Task CheckSlowSaveAsync(XamlRoot root)
    {
        using var transport = new WebDavTransport();
        string path = Path.Combine(AppContext.BaseDirectory, "save-" + Guid.NewGuid().ToString("N") + ".db");
        var sqlite = new SQLiteAsyncConnection(path);
        await sqlite.CreateTableAsync<WebDavSource>();
        await sqlite.CreateTableAsync<WebDavCacheSettings>();
        await sqlite.CreateTableAsync<RemoteTrack>();
        await sqlite.CreateTableAsync<Music>();
        var database = new MusicDatabaseService(sqlite);
        var library = new WebDavLibraryService(database);
        var sources = new WebDavSourcesViewModel(database, library, new RemotePlaybackService(), new RemoteAudioCache(), new AppViewModel(), new LibraryQueries());
        const string password = "synthetic-secret";
        try
        {
            library.SaveGate = Gate();
            library.SaveStarted = Gate();
            await ClickSaveAsync("Slow source");
            await library.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(sources.IsSavingSource && (await database.GetRemoteSourcesAsync()).Count == 0 && library.ScanStarts == 0,
                "closed dialog leaves a visible pending save before SQLite and scan");
            library.SaveGate.TrySetResult();
            await UntilAsync(() => !sources.IsSavingSource && sources.Sources.Count == 1, "successful background commit appears in sources");
            Check(library.SaveCalls == 1 && library.ScanStarts == 1 && library.SavedPassword == password,
                "one native click preserves the immutable password snapshot and starts one scan");

            library.SaveFailure = new IOException("Sensitive fixture payload " + password);
            await ClickSaveAsync("Retry source");
            await UntilAsync(() => !sources.IsSavingSource && sources.SourceSaveError.Length != 0, "source page reports commit failure");
            Check(sources.SourceSaveError.Contains("Retry source", StringComparison.Ordinal) &&
                !sources.SourceSaveError.Contains(password, StringComparison.Ordinal) && sources.Sources.Count == 1,
                "source page failure is useful and excludes raw exception text and credentials");

            library.SaveFailure = null;
            library.SaveGate = Gate();
            library.SaveStarted = Gate();
            await ClickSaveAsync("Retry source");
            await library.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stopping = sources.StopAsync();
            Check(!stopping.IsCompleted, "shutdown waits on the accepted commit after the dialog closes");
            library.SaveGate.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));
            Check(sources.SourceSaveError.Length == 0 && (await database.GetRemoteSourcesAsync()).Count == 2 && library.ScanStarts == 2,
                "retry clears the failure and the accepted commit drains");
        }
        finally
        {
            library.SaveGate?.TrySetResult();
            await sources.StopAsync();
            await sqlite.CloseAsync();
            File.Delete(path);
        }

        async Task ClickSaveAsync(string name)
        {
            using var form = new WebDavConnectionViewModel(transport, library, kind: RemoteSourceKind.Jellyfin)
            {
                Name = name, Address = "http://127.0.0.1:1235/", UserName = "fixture-user", Password = password
            };
            await form.ConnectCommand.ExecuteAsync(null);
            const string label = "Save and scan fixture";
            var dialog = new WebDavConnectionDialog(form, sources.TryQueueSourceSave) { XamlRoot = root, PrimaryButtonText = label };
            var shown = dialog.ShowAsync().AsTask();
            try
            {
                var button = await FindDialogButtonAsync(dialog, label);
                Invoke(button);
                Check(await shown.WaitAsync(TimeSpan.FromSeconds(3)) == ContentDialogResult.Primary,
                    "native Primary closes while the save is gated");
                Check(form.Password.Length == 0, "closed form discards its password");
            }
            finally { if (!shown.IsCompleted) { dialog.Hide(); await shown; } }
        }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Button> FindDialogButtonAsync(ContentDialog dialog, string label)
    {
        Button? button = null;
        await UntilAsync(() => (button = FindControl<Button>(dialog, item => Equals(item.Content, label))) is not null, "native dialog primary button");
        return button!;
    }

    private static void Invoke(Button button)
        => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task CheckCatalogConnectionAsync(XamlRoot root, RemoteSourceKind kind)
    {
        const string previousAddress = "http://127.0.0.1:1234/";
        const string address = "http://127.0.0.1:1235/";
        using var transport = new WebDavTransport();
        var library = new WebDavLibraryService();
        using var source = new WebDavConnectionViewModel(transport, library, kind: kind)
        {
            Name = "Catalog fixture", Address = previousAddress, UserName = "fixture-user"
        };
        WebDavSource? prepared = null;
        string? preparedPassword = null;
        bool Prepare(WebDavSource snapshot, string? password)
        {
            prepared = snapshot;
            preparedPassword = password;
            return true;
        }
        const string label = "Save and scan fixture";
        var dialog = new WebDavConnectionDialog(source, Prepare) { XamlRoot = root, PrimaryButtonText = label };
        var shown = dialog.ShowAsync().AsTask();
        try
        {
            await UntilAsync(() => Equals(dialog.Title, kind.ToString()), "catalog title binds to the selected source kind");
            Check(dialog.TestTree.Parent is FrameworkElement { Visibility: Visibility.Collapsed }, "catalog form hides the folder tree");
            var content = (DependencyObject)dialog.Content;
            var addressBox = FindControl<TextBox>(content, box => box.Text == previousAddress)!;
            var accountBox = FindControl<TextBox>(content, box => box.Text == source.UserName)!;
            Check(!addressBox.IsReadOnly && !accountBox.IsReadOnly, "new catalog identity is editable");
            addressBox.Text = address;
            var passwordBox = FindControl<PasswordBox>(content, _ => true)!;
            passwordBox.Password = "fixture-password";
            var saveButton = await FindDialogButtonAsync(dialog, label);
            Check(source.Address == address && source.Password == passwordBox.Password, "catalog address and password bind as the user edits");
            var nameBox = FindControl<TextBox>(content, box => box.Text == source.Name)!;
            nameBox.Text = "";
            Invoke(saveButton);
            await UntilAsync(() => HasText(content, "MusicSourcesNameRequired"), "blank name reason near the Save button");
            nameBox.Text = "Catalog fixture";
            Invoke(saveButton);
            await UntilAsync(() => HasText(content, "WebDavTestFirst"), "untested form gives a visible retry reason");
            Check(!shown.IsCompleted && prepared is null, "invalid save shows guidance and keeps the native dialog open");
            var testButton = FindControl<Button>(content, button => ReferenceEquals(button.Command, source.ConnectCommand))!;
            library.TestGate = Gate();
            Invoke(testButton);
            await UntilAsync(() => source.IsConnecting && !saveButton.IsEnabled, "test temporarily disables native Primary");
            passwordBox.Password = "changed-password";
            await UntilAsync(() => !source.IsConnecting && saveButton.IsEnabled && testButton.IsEnabled, "editing password cancels the in-flight test");
            library.TestGate.TrySetResult();
            library.TestGate = null;
            Invoke(saveButton);
            await UntilAsync(() => HasText(content, "WebDavTestFirst"), "stale test does not permit a save");
            Invoke(testButton);
            await UntilAsync(() => source.Status == "MusicSourcesTestSucceeded", "catalog test command completes");
            Invoke(saveButton);
            Check(await shown.WaitAsync(TimeSpan.FromSeconds(3)) == ContentDialogResult.Primary &&
                library.Tested is { } tested && tested.Kind == kind && tested.BaseUri == address &&
                prepared is { } saved && saved.Kind == kind && saved.Roots.Length == 0 && preparedPassword == "changed-password",
                "native catalog test and save carry the provider, latest password and no DAV scan roots");
        }
        finally { if (!shown.IsCompleted) { dialog.Hide(); await shown; } }

        var existing = prepared!;
        existing.Id = 1;
        using var edit = new WebDavConnectionViewModel(transport, library, existing);
        var editDialog = new WebDavConnectionDialog(edit, Prepare) { XamlRoot = root, PrimaryButtonText = label };
        shown = editDialog.ShowAsync().AsTask();
        try
        {
            await UntilAsync(() => Equals(editDialog.Title, kind.ToString()), "edit retains the stored source kind");
            var content = (DependencyObject)editDialog.Content;
            Check(FindControl<TextBox>(content, box => box.Text == address)?.IsReadOnly == true &&
                FindControl<TextBox>(content, box => box.Text == existing.UserName)?.IsReadOnly == true,
                "saved catalog address and account are read-only");
            edit.Enabled = false;
            Invoke(await FindDialogButtonAsync(editDialog, label));
            Check(await shown.WaitAsync(TimeSpan.FromSeconds(3)) == ContentDialogResult.Primary && prepared is { Id: 1, Enabled: false } && preparedPassword is null,
                "disabling an existing catalog submits without changing or retesting its credentials");
        }
        finally { if (!shown.IsCompleted) { editDialog.Hide(); await shown; } }

        using var closing = new WebDavConnectionViewModel(transport, library, existing) { Password = "closing-password" };
        bool submittedOnClose = false;
        const string closeLabel = "Cancel fixture";
        var closeDialog = new WebDavConnectionDialog(closing, (_, _) => submittedOnClose = true)
        { XamlRoot = root, PrimaryButtonText = label, CloseButtonText = closeLabel };
        var closeResult = closeDialog.ShowAsync().AsTask();
        library.TestGate = Gate();
        try
        {
            Button? testButton = null;
            await UntilAsync(() => (testButton = FindControl<Button>((DependencyObject)closeDialog.Content,
                button => ReferenceEquals(button.Command, closing.ConnectCommand))) is not null,
                "close fixture compiled test command binds");
            Invoke(testButton!);
            await UntilAsync(() => closing.IsConnecting, "closing fixture has a verification in flight");
            Invoke(await FindDialogButtonAsync(closeDialog, closeLabel));
            Check(await closeResult.WaitAsync(TimeSpan.FromSeconds(3)) == ContentDialogResult.None, "native Cancel closes during connection testing");
            await UntilAsync(() => !closing.IsConnecting, "close cancels the connection request");
            Check(!submittedOnClose && closing.Password.Length == 0 && !closing.CanSubmit, "closed form cannot submit late results or retain the password");
        }
        finally
        {
            library.TestGate.TrySetResult();
            library.TestGate = null;
            if (!closeResult.IsCompleted) { closeDialog.Hide(); await closeResult; }
        }
    }

    private static T? FindControl<T>(DependencyObject node, Func<T, bool> matches) where T : DependencyObject
    {
        if (node is T control && matches(control)) return control;
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            if (FindControl(VisualTreeHelper.GetChild(node, i), matches) is { } found) return found;
        return null;
    }

    private static TreeViewNode Node(string name, bool folder)
    {
        return new TreeViewNode { Content = new WebDavTreeItem("/fixture/" + name, name, folder), HasUnrealizedChildren = folder };
    }

    private static async Task CheckDialogAsync(ContentDialog dialog, TreeView tree, string label)
    {
        tree.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--without-template") >= 0) tree.ItemTemplate = null;
        var root = Node("音乐 / Music", true);
        tree.RootNodes.Add(root);
        var shown = dialog.ShowAsync();
        try
        {
            await UntilAsync(() => HasText(tree, "音乐 / Music"), label + " root name renders");
            Check((tree.ContainerFromNode(root) as TreeViewItem)?.Content is StackPanel, label + " template creates node view");
            Check(HasIcon(tree, "\uE8B7"), label + " folder icon renders");
            root.IsExpanded = true;
            var first = Node("First track.flac", false);
            var second = Node("第二首.mp3", false);
            root.Children.Add(first);
            root.Children.Add(second);
            root.HasUnrealizedChildren = false;
            await UntilAsync(() => HasText(tree, "First track.flac") && HasText(tree, "第二首.mp3"), label + " lazy child names render");
            Check(HasIcon(tree, "\uE8A5"), label + " file icon renders");
            Check(!HasText(tree, "Microsoft.UI.Xaml.Controls.TreeViewNode"), label + " no type-name labels");
            tree.SelectedNodes.Add(second);
            tree.SelectedNodes.Add(first);
            Check(tree.SelectedNodes.Contains(first) && tree.SelectedNodes.Contains(second), label + " multiple nodes select");
            root.IsExpanded = false;
            await UntilAsync(() => tree.ContainerFromNode(first) is null, label + " children collapse");
            root.IsExpanded = true;
            await UntilAsync(() => HasText(tree, "First track.flac") && HasText(tree, "第二首.mp3"), label + " names survive re-expansion");
            await Task.Delay(300); // 等待原生展开动画结束再留存截图。
            await SaveImageAsync(tree, label + "-tree.png");
        }
        finally
        {
            dialog.Hide();
            await shown;
        }
    }

    private static async Task CheckConnectionAsync(XamlRoot xamlRoot)
    {
        const string musicRoot = "/dav/%E9%9F%B3%E4%B9%90/";
        await using var server = new Fixture();
        using var transport = new WebDavTransport();
        var library = new WebDavLibraryService();
        using var vm = new WebDavConnectionViewModel(transport, library, new WebDavSource
        {
            Id = 1, Name = "Fixture", BaseUri = server.Root, Roots = musicRoot
        });
        var dialog = new WebDavConnectionDialog(vm, (_, _) => true) { XamlRoot = xamlRoot };
        var tree = dialog.TestTree;
        var shown = dialog.ShowAsync();
        try
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            var root = tree.RootNodes[0];
            root.IsExpanded = true;
            var music = root.Children[0];
            await UntilAsync(() => FindCheckBox(tree.ContainerFromNode(music)) is not null, "music checkbox");
            var musicCheck = FindCheckBox(tree.ContainerFromNode(music))!;
            Check(PreparedRoots() == musicRoot,
                "restoring the only child must not save its automatically selected parent");
            Check(musicCheck.IsChecked == true, "saved checkbox restored");
            music.IsExpanded = true;
            await UntilAsync(() => music.Children.Count == 2 && FindCheckBox(tree.ContainerFromNode(music.Children[1])) is not null,
                "real lazy loading and child templates");
            var live = FindCheckBox(tree.ContainerFromNode(music.Children[0]))!;
            var studio = FindCheckBox(tree.ContainerFromNode(music.Children[1]))!;
            Check(live.IsChecked == true && studio.IsChecked == true, "lazy children inherit checked parent");
            Toggle(live);
            Check(live.IsChecked == false && studio.IsChecked == true && musicCheck.IsChecked is null,
                "excluding child retains checked sibling and partial parent");
            Check(PreparedRoots() == musicRoot + "studio/", "exclude child from parent scope");
            Toggle(live);
            string siblings = musicRoot + "live/\n" + musicRoot + "studio/";
            Check(PreparedRoots() == siblings && musicCheck.IsChecked is null,
                "checking every child does not expand explicit scope");
            Toggle(musicCheck);
            Check(live.IsChecked == true && studio.IsChecked == true && PreparedRoots() == musicRoot,
                "clicking partial parent explicitly selects subtree");
            Toggle(musicCheck);
            Check(live.IsChecked == false && studio.IsChecked == false && PreparedRoots() is null,
                "unchecking parent clears all descendants");
            Toggle(live);
            Check(PreparedRoots() == musicRoot + "live/", "select only one directory after entire root");
            music.IsExpanded = false;
            music.IsExpanded = true;
            await UntilAsync(() => FindCheckBox(tree.ContainerFromNode(music.Children[0]))?.IsChecked == true,
                "selection survives re-expansion");
            var rootCheck = FindCheckBox(tree.ContainerFromNode(root))!;
            Toggle(rootCheck);
            Check(musicCheck.IsChecked == true && PreparedRoots() == "/dav/",
                "entire root cascades to loaded descendants");
            Toggle(rootCheck);
            Check(PreparedRoots() is null && musicCheck.IsChecked == false, "clearing entire root clears subtree");
            await vm.ConnectCommand.ExecuteAsync(null);
            Check(PreparedRoots() == musicRoot, "reconnect restores original without broadening");
            await UntilAsync(() => HasText(tree, "音乐") && HasIcon(tree, "\uE8B7"), "reconnected names and icons render");
            await Task.Delay(300); // 等待原生展开动画结束再留存截图。
            await SaveImageAsync(tree, "connection-tree.png");
        }
        finally { dialog.Hide(); await shown; }

        string? PreparedRoots() => vm.TryPrepareSave(out var saved, out _) ? saved.Roots : null;
    }

    private static void Toggle(CheckBox check)
    {
        var peer = new Microsoft.UI.Xaml.Automation.Peers.CheckBoxAutomationPeer(check);
        ((Microsoft.UI.Xaml.Automation.Provider.IToggleProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)).Toggle();
    }

    private static CheckBox? FindCheckBox(DependencyObject? node)
    {
        if (node is null) return null;
        if (node is CheckBox { Name: "ScanRootCheckBox" } check) return check;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindCheckBox(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private static bool HasText(DependencyObject node, string text)
    {
        if (node is TextBlock block && block.Text == text && block.ActualWidth > 0 && block.ActualHeight > 0) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasText(VisualTreeHelper.GetChild(node, i), text)) return true;
        return false;
    }

    private static bool HasIcon(DependencyObject node, string glyph)
    {
        if (node is FontIcon icon && icon.Glyph == glyph && icon.ActualWidth > 0 && icon.ActualHeight > 0) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasIcon(VisualTreeHelper.GetChild(node, i), glyph)) return true;
        return false;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task UntilAsync(Func<bool> predicate, string message)
    {
        for (int i = 0; i < 150; i++)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(message);
    }

    private static async Task SaveImageAsync(UIElement element, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
