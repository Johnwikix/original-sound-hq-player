using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

internal static class ConnectionViewModelRegression
{
    public static async Task RunAsync(WebDavTransport transport)
    {
        await using var server = new Fixture(tls: true, nameMismatch: true);
        var library = new WebDavLibraryService();
        using var form = new WebDavConnectionViewModel(transport, library) { Name = "Fixture", Address = server.Root };
        await form.ConnectCommand.ExecuteAsync(null);
        Check(form.HasCertificate && form.TrustCertificateCommand.CanExecute(null) && !await form.SaveAsync(),
            "connection form requires explicit certificate confirmation before saving");
        await form.TrustCertificateCommand.ExecuteAsync(null);
        Check(!form.HasCertificate && form.HasTrustedCertificate && form.Folders.Count == 2 && await form.SaveAsync(),
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
        Check(!restored.HasCertificate && restored.Folders.Count == 2, "editing saved source restores certificate confirmation");
        restored.ForgetCertificateCommand.Execute(null);
        Check(!restored.HasTrustedCertificate && !await restored.SaveAsync(), "forgetting trust invalidates prior connection test");
        await restored.ConnectCommand.ExecuteAsync(null);
        Check(restored.HasCertificate, "forgotten certificate requires confirmation again");
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
