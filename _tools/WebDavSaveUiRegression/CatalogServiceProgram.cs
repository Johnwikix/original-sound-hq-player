using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SQLite;
using Windows.Security.Credentials;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

internal static class CatalogServiceProgram
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new CatalogServiceApp();
        });
    }
}

internal sealed partial class CatalogServiceApp : Application
{
    private static readonly string Report = Path.Combine(AppContext.BaseDirectory, "catalog-service-result.txt");
    private readonly HashSet<string> _vaultEntries = [];
    private const string VaultResource = "OriginalSoundPlayer.MediaCatalog";

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(Report, "STARTED\n");
        var window = new Window { Title = "Remote source service regression", Content = new Grid() };
        WinUIMusicPlayer.App.MainWindow = window;
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
        window.Activate();
        var directory = Directory.CreateTempSubdirectory("remote-catalog-service-");
        AppSettings.MusicCoverCache = Path.Combine(directory.FullName, "Cache");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await VerifyAsync(window.DispatcherQueue, directory.FullName, deadline.Token);
            Log("PASS: real connection forms, source service, Windows Vault, DispatcherQueue, and SQLite library lifecycle");
        }
        catch (Exception ex)
        {
            Log(ex is CheckException ? ex.Message : $"FAIL: {ex.GetType().Name}");
            Environment.ExitCode = 1;
        }
        finally
        {
            foreach (string key in _vaultEntries) RemoveCredential(key);
            directory.Delete(recursive: true);
            window.Close();
            Exit();
        }
    }

    private async Task VerifyAsync(DispatcherQueue dispatcher, string directory, CancellationToken token)
    {
        using var transport = new WebDavTransport();
        await using var davFixture = new Fixture();
        var sqlite = await OpenAsync(directory, mustBeFresh: true);
        WebDavSource jellyfin;
        WebDavSource navidrome;
        WebDavSource dav;
        int[] initialIds;
        try
        {
            var database = await InitializeAsync(sqlite);
            var projection = new AppViewModel(database, dispatcher);
            var diagnostics = new ConnectionDiagnosticsLogger();
            var service = new WebDavLibraryService(database, transport, new RemoteAudioCache(), diagnostics);
            try
            {
                await service.StartAsync(projection);
                await VerifyAddressDiagnosticsAsync(service, diagnostics, token);
                var notifications = new List<bool>();
                service.SourcesChanged += () => notifications.Add(dispatcher.HasThreadAccess);
                dav = new WebDavSource { Name = "WebDAV fixture", BaseUri = davFixture.Root, ReadMetadata = false };
                await service.TestSourceAsync(dav, "", token);
                await service.SaveSourceAsync(dav, "");
                await service.ScanAsync(dav).WaitAsync(token);
                Check(service.GetStatus(dav.Id) is { Phase: "Completed", Found: 1 }, "WebDAV fixture must scan one resource");

                jellyfin = await AddCatalogAsync(service, database, transport, diagnostics, RemoteSourceKind.Jellyfin, token);
                navidrome = await AddCatalogAsync(service, database, transport, diagnostics, RemoteSourceKind.Navidrome, token);
                Check(dav.Id != jellyfin.Id && jellyfin.Id != navidrome.Id && dav.Id != navidrome.Id, "source IDs across providers collided");
                var (music, _) = await database.Versions.ReadLibraryAsync(false);
                Check(music.Count == projection.SongsSource.Count && !projection.PublishedOffUiThread && notifications is [true, true, true],
                    "source or track publication skipped the WinUI dispatcher");
                initialIds = [.. music.Select(song => song.Id).Order()];
                Check(initialIds.Distinct().Count() == music.Count, "Music IDs collided");
                var davTrack = await service.ResolveAsync(music.Single(song => song.SourceId == dav.Id));
                Check(WebDavLibraryService.GetResourceIdentity(davTrack.Track).StartsWith("webdav://", StringComparison.Ordinal),
                    "legacy WebDAV track identity changed");
                foreach (var source in new[] { jellyfin, navidrome })
                {
                    var song = music.First(track => track.SourceId == source.Id && track.ImageHash.Length != 0 && track.Extension == "flac");
                    var (resolved, track) = await service.ResolveAsync(song);
                    Check(resolved.Kind == source.Kind && track.MusicId == song.Id && track.ResourcePath == song.Path
                        && track.ETag.Length == 0, $"{source.Kind} wrong persisted media descriptor");
                    byte[] cover = await service.ReadCoverAsync(song, token);
                    Check(cover.Length > 0, $"{source.Kind} source-service cover load failed");
                    var open = service.CreateAudioOpener(resolved, track);
                    await using var media = await open(0, 3, true, token);
                    Check(media.Message.StatusCode == HttpStatusCode.PartialContent, $"{source.Kind} source-service raw open failed");
                    byte[] marker = new byte[4];
                    await media.Stream.ReadExactlyAsync(marker, token);
                    Check(marker.AsSpan().SequenceEqual("fLaC"u8), $"{source.Kind} source-service changed original FLAC");
                }
            }
            finally { await service.StopAsync(); }
        }
        finally { await sqlite.CloseAsync(); }
        Log("PASS all three source IDs isolated in one library; real authenticated descriptor, raw audio, and cover after sync");

        sqlite = await OpenAsync(directory, mustBeFresh: false);
        try
        {
            var database = await InitializeAsync(sqlite);
            var projection = new AppViewModel(database, dispatcher);
            var service = new WebDavLibraryService(database, transport, new RemoteAudioCache(), NullLogger<WebDavLibraryService>.Instance);
            try
            {
                await service.StartAsync(projection);
                var sources = await database.GetRemoteSourcesAsync();
                Check(sources.Count == 3 && (await database.GetWebDavSourcesAsync()).Single().Id == dav.Id, "provider classification changed after reopen");
                foreach (var source in sources)
                {
                    using var form = new WebDavConnectionViewModel(transport, service, source);
                    if (source.Kind != RemoteSourceKind.WebDav)
                    {
                        await form.ConnectCommand.ExecuteAsync(null).WaitAsync(token);
                        Check(form.Status == "MusicSourcesTestSucceeded", $"{source.Kind} could not restore vault credential");
                    }
                    await service.ScanAsync(source).WaitAsync(token);
                    Check(service.GetStatus(source.Id)?.Phase == "Completed", $"{source.Kind} repeat sync after reopening failed");
                }
                var (reopened, _) = await database.Versions.ReadLibraryAsync(false);
                Check(initialIds.SequenceEqual(reopened.Select(song => song.Id).Order()), "repeat full sync after reopening changed Music IDs");

                var disabled = sources.Single(source => source.Id == jellyfin.Id);
                using var edit = new WebDavConnectionViewModel(transport, service, disabled) { Enabled = false };
                Check(await edit.SaveAsync(), "could not disable Jellyfin source");
                var jellyfinTrack = reopened.First(track => track.SourceId == disabled.Id);
                await ExpectUnavailableAsync(service, jellyfinTrack);

                await service.RemoveSourceAsync(sources.Single(source => source.Id == navidrome.Id));
                Check(!HasCredential(navidrome.CredentialKey), "removal left the Navidrome credential");
                var (remaining, _) = await database.Versions.ReadLibraryAsync(false);
                Check(remaining.All(song => song.SourceId != navidrome.Id)
                    && remaining.Any(song => song.SourceId == jellyfin.Id) && remaining.Any(song => song.SourceId == dav.Id),
                    "remove changed another provider's records");
            }
            finally { await service.StopAsync(); }
        }
        finally { await sqlite.CloseAsync(); }
        sqlite = await OpenAsync(directory, mustBeFresh: false);
        try
        {
            var database = await InitializeAsync(sqlite);
            var sources = await database.GetRemoteSourcesAsync();
            Check(sources.Count == 2 && sources.Single(source => source.Kind == RemoteSourceKind.Jellyfin) is { Enabled: false }
                && sources.Single(source => source.Kind == RemoteSourceKind.WebDav).Id == dav.Id,
                "disable or removal was lost on database restart");
        }
        finally { await sqlite.CloseAsync(); }
        Log("PASS real vault-backed reopen/repeat, persisted disable, removal and credential cleanup");
    }

    private static async Task VerifyAddressDiagnosticsAsync(WebDavLibraryService service, ConnectionDiagnosticsLogger diagnostics,
        CancellationToken token)
    {
        const string address = "http://hidden-user:hidden-userinfo@127.0.0.1:8096/?private-query=hidden-query";
        const string password = "hidden-test-password";
        var source = new WebDavSource { Kind = RemoteSourceKind.Jellyfin, BaseUri = address, UserName = "hidden-test-user" };
        try
        {
            await service.TestSourceAsync(source, password, token);
            throw new CheckException("Malformed address should not pass a connection test.");
        }
        catch (WebDavException ex) when (ex.Code == "InvalidAddress") { }
        var warning = diagnostics.Take();
        Check(!warning.AttachedException && warning.Message.Contains("Jellyfin NormalizeAddress", StringComparison.Ordinal)
            && warning.Message.Contains("WebDavException (", StringComparison.Ordinal)
            && !warning.Message.Contains("hidden-", StringComparison.Ordinal) && !warning.Message.Contains("127.0.0.1", StringComparison.Ordinal),
            "invalid addresses log the stage and exception type without input, credentials, or exception payload");

        try
        {
            await service.TestSourceAsync(source, password, new CancellationToken(canceled: true));
            throw new CheckException("Caller cancellation should still cancel the connection test.");
        }
        catch (OperationCanceledException) { }
        Check(diagnostics.IsEmpty, "caller cancellation does not create a failure warning");
        Log("PASS connection diagnostics redact input and skip caller cancellation");
    }

    private async Task<WebDavSource> AddCatalogAsync(WebDavLibraryService service, MusicDatabaseService database, WebDavTransport transport,
        ConnectionDiagnosticsLogger diagnostics, RemoteSourceKind kind, CancellationToken token)
    {
        string prefix = "REMOTE_CATALOG_" + kind.ToString().ToUpperInvariant() + "_";
        string password = Require(prefix + "PASSWORD", allowEmpty: true);
        using var form = new WebDavConnectionViewModel(transport, service, kind: kind)
        {
            Name = kind.ToString(), Address = Require(prefix + "URL"), UserName = Require(prefix + "USER"),
            Password = Guid.NewGuid().ToString("N")
        };
        await form.ConnectCommand.ExecuteAsync(null).WaitAsync(token);
        Check(form.Status == "WebDavAuthenticationError", $"{kind} invalid credentials not rejected by real connection form");
        var warning = diagnostics.Take();
        // Navidrome 在第一条探活请求上检查凭据。
        string failedStage = kind == RemoteSourceKind.Jellyfin ? "Authenticate" : "ProbeCatalog";
        Check(!warning.AttachedException && warning.Message.Contains($"{kind} {failedStage}", StringComparison.Ordinal)
            && warning.Message.Contains("WebDavException (", StringComparison.Ordinal)
            && !warning.Message.Contains(form.UserName, StringComparison.Ordinal) && !warning.Message.Contains(form.Password, StringComparison.Ordinal)
            && (kind != RemoteSourceKind.Jellyfin || warning.Message.Contains("HTTP 401", StringComparison.Ordinal)),
            $"{kind} authentication diagnostic must contain only failure metadata");
        Check(!await form.SaveAsync() && (await database.GetRemoteSourcesAsync()).All(source => source.Kind != kind),
            $"{kind} invalid login was persisted");
        form.Password = password;
        await form.ConnectCommand.ExecuteAsync(null).WaitAsync(token);
        Check(form.Status == "MusicSourcesTestSucceeded", $"{kind} real authenticated connection failed: {form.Status}");
        Check(await form.SaveAsync() && form.SavedSource is not null, $"{kind} real source form could not save");
        var source = form.SavedSource!;
        _vaultEntries.Add(source.CredentialKey);
        Check(HasCredential(source.CredentialKey), $"{kind} save did not publish its isolated vault entry");
        await service.ScanAsync(source).WaitAsync(token);
        var status = service.GetStatus(source.Id);
        Check(status is { Phase: "Completed", Found: > 0 }, $"{kind} source scan failed: {status?.Error ?? status?.Phase}");
        Log($"PASS {kind} rejected failed login, recovered, saved, and scanned {status!.Found} tracks");
        return source;
    }

    private static async Task ExpectUnavailableAsync(WebDavLibraryService service, Music song)
    {
        try
        {
            await service.ResolveAsync(song);
            throw new CheckException("Disabled source resolved for playback.");
        }
        catch (WebDavException ex) when (ex.Code == "SourceUnavailable") { }
    }

    private static Task<SQLiteAsyncConnection> OpenAsync(string directory, bool mustBeFresh) =>
        LibraryDatabaseMigration.OpenAsync(null, directory, async (db, upgrading) =>
        {
            Check(upgrading == mustBeFresh, "isolated database generation did not match expected startup path");
            await db.CreateTableAsync<Music>();
            await db.CreateTableAsync<MusicLyrics>();
            await db.CreateTableAsync<MusicLyricsRecord>();
            await db.CreateTableAsync<LyricsSearchStateRecord>();
            await db.CreateTableAsync<PlayListMusic>();
            await MusicDatabaseService.EnsureRemoteCatalogSchemaAsync(db, upgrading);
        }, _ => throw new CheckException("Unexpected backup for fresh or same-generation isolated DB."));

    private static async Task<MusicDatabaseService> InitializeAsync(SQLiteAsyncConnection sqlite)
    {
        var database = new MusicDatabaseService(sqlite);
        await database.Versions.InitializeAsync();
        await database.Lyrics.InitializeAsync();
        return database;
    }

    private static string Require(string name, bool allowEmpty = false) =>
        Environment.GetEnvironmentVariable(name) is { } value && (allowEmpty || value.Length > 0)
            ? value : throw new CheckException($"Missing fixture environment variable {name}.");

    private static bool HasCredential(string key)
    {
        try { return new PasswordVault().Retrieve(VaultResource, key) is not null; }
        catch { return false; }
    }

    private static void RemoveCredential(string key)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(VaultResource, key));
        }
        catch { }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new CheckException("FAIL: " + description);
    }

    private static void Log(string message) => File.AppendAllText(Report, message + "\n");
    private sealed class CheckException(string description) : Exception(description);

    private sealed class ConnectionDiagnosticsLogger : ILogger<WebDavLibraryService>
    {
        private readonly ConcurrentQueue<(string Message, bool AttachedException)> _warnings = new();
        public bool IsEmpty => _warnings.IsEmpty;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level == LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            string message = formatter(state, exception);
            if (message.StartsWith("Remote source connection test failed:", StringComparison.Ordinal))
                _warnings.Enqueue((message, exception is not null));
        }

        public (string Message, bool AttachedException) Take() => _warnings.TryDequeue(out var warning)
            ? warning : throw new CheckException("Expected a redacted connection failure diagnostic.");
    }
}
