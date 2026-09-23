using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

internal static class ConnectionViewModelRegression
{
    public static async Task RunTreeAsync(WebDavTransport transport)
    {
        await using var server = new Fixture();
        var library = new WebDavLibraryService();
        using var form = new WebDavConnectionViewModel(transport, library) { Name = "Tree", Address = server.Root };
        await form.ConnectCommand.ExecuteAsync(null);
        Check(form.Folders.Count == 1 && form.Folders[0].IsLoaded && form.Folders[0].IsSelected &&
            form.Folders[0].Children.Count == 1, "connection shows selected root with lazily loaded child folders");
        var root = form.Folders[0];
        var musicFolder = root.Children[0];
        await form.LoadChildrenAsync(musicFolder);
        Check(musicFolder.IsLoaded && musicFolder.Children.Count == 2 &&
            musicFolder.Children[0].Href == "/dav/%E9%9F%B3%E4%B9%90/live/", "expanding loads a nested directory");
        form.SetSelected(root, false);
        form.SetSelected(musicFolder, false);
        form.SetSelected(musicFolder.Children[0], true);
        Check(await form.SaveAsync() && library.Saved?.Roots == "/dav/%E9%9F%B3%E4%B9%90/live/",
            "saving a nested selection keeps its exact path");
        using var edited = new WebDavConnectionViewModel(transport, library, library.Saved);
        await edited.ConnectCommand.ExecuteAsync(null);
        Check(edited.Folders[0].Children[0].IsExpanded && edited.Folders[0].Children[0].Children[0].IsSelected &&
            await edited.SaveAsync() && library.Saved?.Roots == "/dav/%E9%9F%B3%E4%B9%90/live/",
            "editing reveals and preserves a nested selected root");
        edited.SetSelected(edited.Folders[0].Children[0].Children[1], true);
        Check(await edited.SaveAsync() && library.Saved?.Roots ==
            "/dav/%E9%9F%B3%E4%B9%90/live/\n/dav/%E9%9F%B3%E4%B9%90/studio/",
            "multiple nested roots are saved in tree order");
        edited.SetSelected(edited.Folders[0], true);
        Check(await edited.SaveAsync() && library.Saved?.Roots == "/dav/",
            "selected parent removes duplicate child roots");
    }

    public static async Task RunAsync(WebDavTransport transport)
    {
        await using var server = new Fixture(tls: true, nameMismatch: true);
        var library = new WebDavLibraryService();
        using var form = new WebDavConnectionViewModel(transport, library) { Name = "Fixture", Address = server.Root };
        await form.ConnectCommand.ExecuteAsync(null);
        Check(form.HasCertificate && form.TrustCertificateCommand.CanExecute(null) && !await form.SaveAsync(),
            "connection form requires explicit certificate confirmation before saving");
        await form.TrustCertificateCommand.ExecuteAsync(null);
        Check(!form.HasCertificate && form.HasTrustedCertificate && form.Folders.Count == 1 &&
            form.Folders[0].Children.Count == 1 && await form.SaveAsync(),
            "confirmation reconnects and saves source certificate identity");
        var saved = library.Saved!;
        Check(saved.TrustedCertificateOrigin == new Uri(server.Root).GetLeftPart(UriPartial.Authority) && saved.TrustedCertificateSha256.Length == 64,
            "saved trust includes exact HTTPS origin and SHA-256 fingerprint");
        using (var database = new SQLite.SQLiteConnection(":memory:"))
        {
            database.Execute("CREATE TABLE WebDavSource (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT, BaseUri TEXT)");
            database.Execute("INSERT INTO WebDavSource (Name, BaseUri) VALUES (?, ?)", "Existing source", server.Root);
            database.CreateTable<WinUIMusicPlayer.Model.WebDavSource>();
            var old = database.Find<WinUIMusicPlayer.Model.WebDavSource>(1);
            Check(string.IsNullOrEmpty(old.TrustedCertificateSha256), "existing database sources migrate without implicitly trusting a certificate");
            database.Insert(saved);
            var persisted = database.Find<WinUIMusicPlayer.Model.WebDavSource>(saved.Id);
            Check(persisted.TrustedCertificateOrigin == saved.TrustedCertificateOrigin && persisted.TrustedCertificateSha256 == saved.TrustedCertificateSha256,
                "SQLite persists certificate origin and fingerprint using production source model");
        }
        using var restored = new WebDavConnectionViewModel(transport, library, saved);
        await restored.ConnectCommand.ExecuteAsync(null);
        Check(!restored.HasCertificate && restored.Folders.Count == 1 && restored.Folders[0].IsSelected,
            "editing saved source restores certificate confirmation and root selection");
        restored.ForgetCertificateCommand.Execute(null);
        Check(!restored.HasTrustedCertificate && !await restored.SaveAsync(), "forgetting trust invalidates prior connection test");
        await restored.ConnectCommand.ExecuteAsync(null);
        Check(restored.HasCertificate, "forgotten certificate requires confirmation again");
        var nestedSource = new WinUIMusicPlayer.Model.WebDavSource
        {
            Id = saved.Id, Name = saved.Name, BaseUri = saved.BaseUri, UserName = saved.UserName,
            TrustedCertificateOrigin = saved.TrustedCertificateOrigin,
            TrustedCertificateSha256 = saved.TrustedCertificateSha256,
            Roots = "/dav/%E9%9F%B3%E4%B9%90/live/"
        };
        using var nested = new WebDavConnectionViewModel(transport, library, nestedSource);
        await nested.ConnectCommand.ExecuteAsync(null);
        var root = nested.Folders[0];
        var musicFolder = root.Children[0];
        Check(!root.IsSelected && musicFolder.IsExpanded && musicFolder.Children.Count == 2 &&
            musicFolder.Children[0].IsSelected && await nested.SaveAsync() &&
            library.Saved?.Roots == "/dav/%E9%9F%B3%E4%B9%90/live/",
            "editing restores a nested selected folder without broadening the scan root");
        nested.SetSelected(root, true);
        Check(await nested.SaveAsync() && library.Saved?.Roots == "/dav/",
            "selected ancestor removes duplicate descendant roots");
        form.Address = server.Root + "different/";
        Check(!form.HasTrustedCertificate && !await form.SaveAsync(), "editing address clears certificate confirmation and tested connection");
        using var changing = new WebDavConnectionViewModel(transport, library) { Address = server.Root };
        Task pending = changing.ConnectCommand.ExecuteAsync(null);
        changing.Address = "https://localhost:1/";
        await pending;
        Check(!changing.HasCertificate && changing.Folders.Count == 0, "late TLS result cannot repopulate a changed connection form");
        using var closing = new WebDavConnectionViewModel(transport, library) { Address = server.Root };
        Task closingRequest = closing.ConnectCommand.ExecuteAsync(null);
        closing.Dispose();
        await closingRequest;
        Check(!closing.HasCertificate && !await closing.SaveAsync(), "closed connection form cancels in-flight TLS and cannot save");
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS: " + name);
    }
}
