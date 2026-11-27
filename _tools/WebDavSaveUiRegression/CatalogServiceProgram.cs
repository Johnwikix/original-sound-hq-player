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
using WinUIMusicPlayer.Services.RemoteSources;
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
            if (Environment.GetEnvironmentVariable("REMOTE_CATALOG_LOCAL_SAVE_AND_SCAN") == "1")
                await VerifyLocalSaveAndScanAsync(window.DispatcherQueue, directory.FullName, deadline.Token);
            else if (Environment.GetEnvironmentVariable("REMOTE_CATALOG_JELLYFIN_COVER_ONLY") == "1")
                await VerifyJellyfinCoversAsync(window.DispatcherQueue, directory.FullName, deadline.Token);
            else if (Environment.GetEnvironmentVariable("REMOTE_CATALOG_JELLYFIN_RESTORED_COVER_ID") is { Length: > 0 } restored)
                await VerifyJellyfinRestoredCoverAsync(window.DispatcherQueue, directory.FullName, restored, deadline.Token);
            else
            {
                await VerifyAsync(window.DispatcherQueue, directory.FullName, deadline.Token);
                Log("PASS: real connection forms, source service, Windows Vault, DispatcherQueue, and SQLite library lifecycle");
            }
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

    private static async Task VerifyLocalSaveAndScanAsync(DispatcherQueue dispatcher, string directory, CancellationToken token)
    {
        using var transport = new WebDavTransport();
        await using var server = new Fixture();
        var sqlite = await OpenAsync(directory, mustBeFresh: true);
        try
        {
            var database = await InitializeAsync(sqlite);
            var projection = new AppViewModel(database, dispatcher);
            var service = new WebDavLibraryService(database, transport, new RemoteAudioCache(), NullLogger<WebDavLibraryService>.Instance);
            try
            {
                await service.StartAsync(projection);
                using var form = new WebDavConnectionViewModel(transport, service)
                {
                    Address = server.Root, Name = "Local save and scan", ReadMetadata = false
                };
                await form.ConnectCommand.ExecuteAsync(null).WaitAsync(token);
                Check(form.TryPrepareSave(out var source, out var password), "real loopback connection did not prepare a save snapshot");
                form.Dispose();
                await sqlite.ExecuteAsync("CREATE TRIGGER FailFixtureSource BEFORE INSERT ON WebDavSource BEGIN SELECT RAISE(ABORT, 'fixture save failure'); END");
                try
                {
                    await service.SaveAndScanSourceAsync(source, password).WaitAsync(token);
                    throw new CheckException("Expected a fixture database failure.");
                }
                catch (SQLiteException) { }
                Check(source.Id == 0 && (await database.GetRemoteSourcesAsync()).Count == 0 && service.GetStatus(source.Id) is null,
                    "failed real transaction left a source or scan");
                await sqlite.ExecuteAsync("DROP TRIGGER FailFixtureSource");

                server.FailDirectoryListings = true;
                await service.SaveAndScanSourceAsync(source, password).WaitAsync(token);
                Check((await database.GetRemoteSourcesAsync()).Single().Id == source.Id, "retry did not durably save the prepared source exactly once");
                await service.GetScheduledScanForFixture(source.Id).WaitAsync(token);
                Check(service.GetStatus(source.Id) is { Phase: "Failed", Error: "RequestFailed" }
                    && (await database.Versions.ReadLibraryAsync(false)).Music.Count == 0,
                    "the first scan hid the provider failure or imported tracks while the server failed");
                server.FailDirectoryListings = false;
                await service.ScanAsync(source).WaitAsync(token);
                Check(service.GetStatus(source.Id) is { Phase: "Completed", Found: 1 }
                    && (await database.Versions.ReadLibraryAsync(false)).Music.Count == 1
                    && (await database.GetRemoteSourcesAsync()).Single().Id == source.Id,
                    "scan retry after provider recovery did not import exactly one song under the saved source");
                Log("PASS: real loopback, prepared and closed form, SQLite failure and retry, provider failure and manual scan retry");
            }
            finally { await service.StopAsync(); }
        }
        finally { await sqlite.CloseAsync(); }
    }

    private async Task VerifyJellyfinCoversAsync(DispatcherQueue dispatcher, string directory, CancellationToken token)
    {
        using var transport = new WebDavTransport();
        var sqlite = await OpenAsync(directory, mustBeFresh: true);
        try
        {
            var database = await InitializeAsync(sqlite);
            var projection = new AppViewModel(database, dispatcher);
            var service = new WebDavLibraryService(database, transport, new RemoteAudioCache(), NullLogger<WebDavLibraryService>.Instance);
            try
            {
                await service.StartAsync(projection);
                var source = new WebDavSource
                {
                    Kind = RemoteSourceKind.Jellyfin, Name = "Jellyfin test",
                    BaseUri = Require("REMOTE_CATALOG_JELLYFIN_URL"), UserName = Require("REMOTE_CATALOG_JELLYFIN_USER")
                };
                string password = Require("REMOTE_CATALOG_JELLYFIN_PASSWORD", allowEmpty: true);
                await service.TestSourceAsync(source, password, token);
                await service.SaveSourceAsync(source, password);
                _vaultEntries.Add(source.CredentialKey);
                await service.ScanAsync(source).WaitAsync(token);
                Check(service.GetStatus(source.Id) is { Phase: "Completed" }, "Jellyfin fixture scan failed");
                var missing = await FindTrackAsync("REMOTE_CATALOG_JELLYFIN_MISSING_IMAGE_ID");
                var noCandidate = await FindTrackAsync("REMOTE_CATALOG_JELLYFIN_NO_IMAGE_ITEM_ID");
                var (music, _) = await database.Versions.ReadLibraryAsync(false);
                byte[]? found = null;
                var requested = new HashSet<string>();
                foreach (var song in music)
                {
                    var remote = await database.GetRemoteTrackAsync(song.Id);
                    if (remote is null || string.IsNullOrEmpty(remote.CoverId) || !requested.Add(remote.CoverId)) continue;
                    var candidate = await service.ReadCoverResultAsync(song, token);
                    if (candidate.Bytes.Length == 0) continue;
                    Check(!candidate.CanMatch, "positive server art permitted external matching");
                    found = candidate.Bytes;
                    break;
                }
                Check(found is { Length: > 0 }, "expected server art in the Jellyfin test library");
                string missingAlbum = WebDavCachePaths.GetServerCoverKey(missing.ImageHash);
                var sibling = music.FirstOrDefault(song => song.Id != missing.Id && song.Title != missing.Title &&
                    WebDavCachePaths.GetServerCoverKey(song.ImageHash) == missingAlbum);
                Check(sibling is not null, "expected another track in the missing-art album fixture");
                await CheckJellyfinCoverCacheAsync(service, source, missing, sibling!, noCandidate, found!, token);
                Log("PASS: Jellyfin server art, confirmed 404, legacy cache and key, match reuse and precedence, cache clear, and offline gate");

                async Task<Music> FindTrackAsync(string variable)
                {
                    var result = await database.GetRemoteMusicAsync(source.Id, Require(variable));
                    return result ?? throw new CheckException("Expected Jellyfin cover fixture track ID.");
                }
            }
            finally { await service.StopAsync(); }
        }
        finally { await sqlite.CloseAsync(); }
    }

    private async Task VerifyJellyfinRestoredCoverAsync(DispatcherQueue dispatcher, string directory, string itemId, CancellationToken token)
    {
        itemId = Guid.Parse(itemId).ToString("N");
        string albumId = Environment.GetEnvironmentVariable("REMOTE_CATALOG_JELLYFIN_PREVIOUS_ALBUM_ID") is { Length: > 0 } album
            ? Guid.Parse(album).ToString("N") : "";
        using var transport = new WebDavTransport();
        var sqlite = await OpenAsync(directory, mustBeFresh: true);
        try
        {
            var database = await InitializeAsync(sqlite);
            var projection = new AppViewModel(database, dispatcher);
            var service = new WebDavLibraryService(database, transport, new RemoteAudioCache(), NullLogger<WebDavLibraryService>.Instance);
            try
            {
                await service.StartAsync(projection);
                var source = new WebDavSource
                {
                    Kind = RemoteSourceKind.Jellyfin, Name = "Restored cover fixture",
                    BaseUri = Require("REMOTE_CATALOG_JELLYFIN_URL"), UserName = Require("REMOTE_CATALOG_JELLYFIN_USER")
                };
                string password = Require("REMOTE_CATALOG_JELLYFIN_PASSWORD", allowEmpty: true);
                await service.TestSourceAsync(source, password, token);
                await service.SaveSourceAsync(source, password);
                _vaultEntries.Add(source.CredentialKey);

                // Start at the previous catalog state without item art. Its optional album candidate had no primary image either.
                var old = new CatalogTrack(itemId, "Previously missing cover", "Fixture artist", "Fixture album", albumId, "", "",
                    1, 1, 0, TimeSpan.Zero, "flac", -1, albumId, "");
                var imported = await database.CommitCatalogTracksAsync(source, "previous-cover-fixture", [old], token);
                var playing = imported.Single();
                projection.SongsSource.Add(playing);
                string oldHash = playing.ImageHash;
                string cacheRoot = AppSettings.MusicCoverCache;
                Directory.CreateDirectory(WebDavCachePaths.Covers(cacheRoot));
                string oldRaw = WebDavCachePaths.Cover(cacheRoot, oldHash);
                string oldMiss = WebDavCachePaths.MissingCover(cacheRoot, oldHash);
                await File.WriteAllBytesAsync(oldRaw, [], token);
                await File.WriteAllBytesAsync(oldMiss, [], token);
                byte[] fallback = [1, 2, 3, 4];
                await service.CacheMatchedCoverAsync(playing, oldHash, fallback, token);
                string oldMatch = WebDavCachePaths.MatchedCover(cacheRoot, oldHash);
                Check(File.Exists(oldMatch) && (await service.ReadCoverAsync(playing, token)).AsSpan().SequenceEqual(fallback),
                    "previous Jellyfin fallback was not present before resync");

                await service.ScanAsync(source).WaitAsync(token);
                var updated = await database.GetRemoteMusicAsync(source.Id, itemId);
                var remote = await database.GetRemoteTrackAsync(playing.Id);
                Check(service.GetStatus(source.Id) is { Phase: "Completed" } && updated is not null && updated.Id == playing.Id
                    && remote?.CoverId == itemId && playing.ImageHash == updated.ImageHash && updated.ImageHash != oldHash
                    && WebDavCachePaths.GetServerCoverKey(updated.ImageHash) != WebDavCachePaths.GetServerCoverKey(oldHash),
                    "resync did not publish the same song's new Jellyfin item cover key");
                var (bytes, canMatch) = await service.ReadCoverResultAsync(playing, token);
                bool validImage = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ||
                    bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 });
                string freshRaw = WebDavCachePaths.Cover(cacheRoot, playing.ImageHash);
                Check(validImage && !canMatch && File.Exists(freshRaw) && File.Exists(oldRaw) && File.Exists(oldMiss) && File.Exists(oldMatch)
                    && WebDavCachePaths.FindCover(cacheRoot, playing.ImageHash) == freshRaw,
                    "the player did not use the server image while the previous miss and fallback were still cached");
                Log($"PASS: resync replaced the previous cover cache key with Jellyfin item art ({bytes.Length} bytes), keeping track ID and previous cache files");
            }
            finally { await service.StopAsync(); }
        }
        finally { await sqlite.CloseAsync(); }
    }

    private static async Task CheckJellyfinCoverCacheAsync(WebDavLibraryService service, WebDavSource source, Music missing,
        Music sibling, Music noCandidate, byte[] serverArt, CancellationToken token)
    {
        string root = AppSettings.MusicCoverCache;
        Directory.CreateDirectory(WebDavCachePaths.Covers(root));
        string raw = WebDavCachePaths.Cover(root, missing.ImageHash);
        string negative = WebDavCachePaths.MissingCover(root, missing.ImageHash);
        string matched = WebDavCachePaths.MatchedCover(root, missing.ImageHash);
        // Isolate this 404 from optional catalog probing for a song whose album may happen to be reused.
        if (File.Exists(negative)) File.Delete(negative);
        await File.WriteAllBytesAsync(raw, [], token);
        var missingResult = await service.ReadCoverResultAsync(missing, token);
        Check(missingResult.CanMatch && missingResult.Bytes.Length == 0 && File.Exists(negative),
            "legacy zero cache did not resolve into a confirmed 404");
        byte[] fixtureMatch = [1, 2, 3, 4];
        await service.CacheMatchedCoverAsync(missing, "outdated-hash", fixtureMatch, token);
        Check(!File.Exists(matched), "a stale matching result reached the cache");
        await service.CacheMatchedCoverAsync(missing, missing.ImageHash, fixtureMatch, token);
        Check(await service.ReadCoverResultAsync(missing, token) is { CanMatch: false } cached &&
            cached.Bytes.AsSpan().SequenceEqual(fixtureMatch) && WebDavCachePaths.FindCover(root, missing.ImageHash) == matched,
            "matched cover was not reused");
        Check(await service.ReadCoverResultAsync(sibling, token) is { CanMatch: true, Bytes.Length: 0 },
            "another track inherited its sibling's matched cover");
        byte[] differentMatch = [8, 7, 6];
        await service.CacheMatchedCoverAsync(sibling, sibling.ImageHash, differentMatch, token);
        Check((await service.ReadCoverResultAsync(sibling, token)).Bytes.AsSpan().SequenceEqual(differentMatch),
            "same-album sibling could not retain a different matched cover");
        await File.WriteAllBytesAsync(raw, serverArt, token);
        Check(await service.ReadCoverResultAsync(missing, token) is { CanMatch: false } preferred &&
            preferred.Bytes.AsSpan().SequenceEqual(serverArt) && WebDavCachePaths.FindCover(root, missing.ImageHash) == raw,
            "server raw art did not retain priority over the match");

        string newKey = noCandidate.ImageHash;
        noCandidate.ImageHash = "";
        Check((await service.ReadCoverResultAsync(noCandidate, token)).CanMatch,
            "an older track without ImageHash could not match");
        await service.CacheMatchedCoverAsync(noCandidate, "", fixtureMatch, token);
        Check((await service.ReadCoverResultAsync(noCandidate, token)).Bytes.AsSpan().SequenceEqual(fixtureMatch)
            && File.Exists(WebDavCachePaths.MatchedCover(root, newKey)), "legacy track could not reuse its new stable match key");
        await service.ClearCoverCacheAsync(root);
        Check(!File.Exists(raw) && !File.Exists(matched) && !File.Exists(negative), "clearing covers left a Jellyfin cache entry");
        service.ReportSourceFailure(source.Id);
        Check(await service.ReadCoverResultAsync(noCandidate, token) is { CanMatch: false, Bytes.Length: 0 },
            "an unavailable Jellyfin source triggered an external match");
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
                Check(await CommitSourceAsync(service, edit) is not null, "could not disable Jellyfin source");
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
        Check(!form.TryPrepareSave(out _, out _) && (await database.GetRemoteSourcesAsync()).All(source => source.Kind != kind),
            $"{kind} invalid login was persisted");
        form.Password = password;
        await form.ConnectCommand.ExecuteAsync(null).WaitAsync(token);
        Check(form.Status == "MusicSourcesTestSucceeded", $"{kind} real authenticated connection failed: {form.Status}");
        var source = await CommitSourceAsync(service, form) ?? throw new CheckException($"{kind} real source form could not save");
        _vaultEntries.Add(source.CredentialKey);
        Check(HasCredential(source.CredentialKey), $"{kind} save did not publish its isolated vault entry");
        await WaitForScanCompletionAsync(service, source.Id, token);
        var status = service.GetStatus(source.Id);
        Check(status is { Phase: "Completed", Found: > 0 }, $"{kind} source scan failed: {status?.Error ?? status?.Phase}");
        Log($"PASS {kind} rejected failed login, recovered, saved, and scanned {status!.Found} tracks");
        return source;
    }

    private static async Task<WebDavSource?> CommitSourceAsync(WebDavLibraryService service, WebDavConnectionViewModel form)
    {
        if (!form.TryPrepareSave(out var source, out var password)) return null;
        await service.SaveAndScanSourceAsync(source, password);
        return source;
    }

    private static async Task WaitForScanCompletionAsync(WebDavLibraryService service, int sourceId, CancellationToken token)
    {
        while (service.GetStatus(sourceId)?.Phase is not ("Completed" or "Failed" or "Cancelled"))
            await Task.Delay(20, token);
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
