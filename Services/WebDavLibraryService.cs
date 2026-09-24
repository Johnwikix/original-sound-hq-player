using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

public sealed record WebDavScanStatus(int SourceId, string Phase, int Found, int Tagged, string? Error = null);

/// <summary>来源同步与元数据的应用级所有者；页面离开后继续，应用退出时显式停止。</summary>
public sealed class WebDavLibraryService(MusicDatabaseService database, WebDavTransport transport,
    RemoteAudioCache cache, ILogger<WebDavLibraryService> logger)
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, (CancellationTokenSource Cancel, Task Work)> _scans = [];
    private readonly Dictionary<int, Task> _probes = [];
    private readonly SemaphoreSlim _metadataSlots = new(2, 2);
    private readonly SemaphoreSlim _coverSlot = new(1, 1);
    // Older readers cached DSF as coverless. Recheck these misses once per bounded working set.
    // Access is serialized by _coverSlot; never retain artwork or Music instances here.
    private readonly HashSet<string> _checkedDsfCoverMisses = [];
    private readonly SemaphoreSlim _lyricsSlot = new(1, 1);
    private readonly Dictionary<int, WebDavScanStatus> _statuses = [];
    private readonly HashSet<int> _offlineSources = [];
    private readonly Dictionary<int, long> _publishedAt = [];
    private readonly object _gate = new();
    private Task? _availabilityLoop;
    private AppViewModel? _library;
    private WebDavCacheSettings _cacheSettings = new();
    public CancellationToken StoppingToken => _stop.Token;
    public event Action<WebDavScanStatus>? StatusChanged;
    public event Action<int, bool>? SourceAvailabilityChanged;
    public event Action? SourcesChanged;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".aiff", ".aif", ".ogg", ".oga", ".opus", ".ape", ".wv", ".dsf", ".dff", ".wma" };

    public async Task StartAsync(AppViewModel library)
    {
        _library = library;
        await ApplyCacheSettingsAsync(await database.GetWebDavCacheSettingsAsync(), false);
        if (!_stop.IsCancellationRequested) library.State.Preferences.PropertyChanged += OnPreferencesChanged;
    }

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_stop.IsCancellationRequested && e.PropertyName == nameof(AppSettings.MusicCoverCache)) ConfigureCache();
    }

    private void ConfigureCache() => cache.Configure(_cacheSettings.Enabled, AppSettings.MusicCoverCache,
        _cacheSettings.LimitGiB * 1024L * 1024 * 1024);
    public async Task StartConfiguredScansAsync()
    {
        var sources = await database.GetWebDavSourcesAsync();
        lock (_gate)
        {
            _availabilityLoop ??= Task.Run(() => MonitorAvailabilityAsync(_stop.Token));
        }
        foreach (var source in sources)
            if (source.Enabled && source.ScanOnStartup) _ = ScanAsync(source);
    }
    public WebDavScanStatus? GetStatus(int sourceId) { lock (_gate) return _statuses.GetValueOrDefault(sourceId); }
    public bool IsSourceOffline(int sourceId) { lock (_gate) return _offlineSources.Contains(sourceId); }

    /// <summary>对单个来源执行一次轻量在线检查；不会扫描目录内容。</summary>
    public Task ProbeAsync(WebDavSource source, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_probes.TryGetValue(source.Id, out var previous) && !previous.IsCompleted) return previous;
            // PasswordVault 读取和 URI 校验属于同步前置工作，避免在右键菜单/浏览器 UI 线程执行。
            var work = Task.Run(() => ProbeCoreAsync(source, cancellationToken));
            _probes[source.Id] = work;
            return work;
        }
    }

    public async Task<bool> EnsureAvailableAsync(WebDavSource source, CancellationToken cancellationToken = default)
    {
        await ProbeAsync(source, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        return !IsSourceOffline(source.Id);
    }

    private async Task ProbeCoreAsync(WebDavSource source, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        try
        {
            await transport.ProbeAsync(Connect(source), linked.Token).ConfigureAwait(false);
            PublishAvailability(source.Id, false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex)
        {
            string error = ex is WebDavException dav ? dav.Code : "ConnectionFailed";
            logger.LogWarning("WebDAV availability probe failed for source {SourceId}: {Error}", source.Id, error);
            PublishAvailability(source.Id, true);
        }
    }

    private async Task MonitorAvailabilityAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var sources = await database.GetWebDavSourcesAsync().ConfigureAwait(false);
                    var probes = new List<Task>(sources.Count);
                    foreach (var source in sources)
                        if (source.Enabled) probes.Add(ProbeAsync(source, token));
                    await Task.WhenAll(probes).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "WebDAV availability monitoring iteration failed"); }

                TimeSpan delay = _library?.CurrentPlayingMusic?.IsRemote == true
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromMinutes(5);
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Publish(WebDavScanStatus status)
    {
        lock (_gate)
        {
            var previous = _statuses.GetValueOrDefault(status.SourceId);
            _statuses[status.SourceId] = status;
            long now = Environment.TickCount64;
            if (previous?.Phase == status.Phase && now - _publishedAt.GetValueOrDefault(status.SourceId) < 250) return;
            _publishedAt[status.SourceId] = now;
        }
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_stop.IsCancellationRequested) return;
            StatusChanged?.Invoke(status);
        });
    }

    private void PublishAvailability(int sourceId, bool offline)
    {
        bool changed;
        lock (_gate)
        {
            changed = offline ? _offlineSources.Add(sourceId) : _offlineSources.Remove(sourceId);
        }
        if (!changed) return;
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_stop.IsCancellationRequested) return;
            if (_library is not null)
            {
                foreach (var music in _library.SongsSource)
                    if (music.SourceId == sourceId) music.IsRemoteOffline = offline;
                foreach (var music in _library.CurrentPlayingList)
                    if (music.SourceId == sourceId) music.IsRemoteOffline = offline;
                if (_library.CurrentPlayingMusic?.SourceId == sourceId)
                    _library.CurrentPlayingMusic.IsRemoteOffline = offline;
            }
            SourceAvailabilityChanged?.Invoke(sourceId, offline);
        });
    }
    public WebDavConnection Connect(WebDavSource source)
    {
        string password = "";
        if (source.UserName.Length != 0)
        {
            try
            {
                var credential = new PasswordVault().Retrieve("OriginalSoundPlayer.WebDav", source.CredentialKey);
                credential.RetrievePassword();
                password = credential.Password;
            }
            catch { throw new WebDavException("AuthenticationRequired"); }
        }
        var trust = string.IsNullOrEmpty(source.TrustedCertificateSha256) ? null
            : new WebDavCertificateTrust(source.TrustedCertificateOrigin, source.TrustedCertificateSha256);
        return new(WebDavTransport.NormalizeRoot(source.BaseUri), source.UserName, password, trust);
    }
    public async Task SaveSourceAsync(WebDavSource source, string password)
    {
        source.BaseUri = WebDavTransport.NormalizeRoot(source.BaseUri).AbsoluteUri;
        if (string.IsNullOrWhiteSpace(source.Name)) throw new WebDavException("NameRequired");
        foreach (var existing in await database.GetWebDavSourcesAsync())
            if (existing.Id != source.Id && existing.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)) throw new WebDavException("NameExists");
        if (source.UserName.Length != 0) new PasswordVault().Add(new PasswordCredential("OriginalSoundPlayer.WebDav", source.CredentialKey, password));
        await database.SaveWebDavSourceAsync(source);
        if (_library is not null) await _library.RefreshSongsSourceAsync();
        if (source.Enabled) _ = ProbeAsync(source);
        SourcesChanged?.Invoke();
    }
    public Task ScanAsync(WebDavSource source)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || !source.Enabled) return Task.CompletedTask;
            if (_scans.TryGetValue(source.Id, out var previous) && !previous.Work.IsCompleted) return previous.Work;
            previous.Cancel?.Dispose();
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var work = Task.Run(() => ScanCoreAsync(source, cancel.Token));
            _scans[source.Id] = (cancel, work);
            return work;
        }
    }
    public async Task CancelScanAsync(int sourceId)
    {
        Task? work = null;
        lock (_gate)
            if (_scans.TryGetValue(sourceId, out var scan)) { scan.Cancel.Cancel(); work = scan.Work; }
        if (work is not null) await work;
    }
    public async Task RemoveSourceAsync(WebDavSource source)
    {
        await CancelScanAsync(source.Id);
        await database.RemoveWebDavSourceAsync(source.Id);
        try { var vault = new PasswordVault(); vault.Remove(vault.Retrieve("OriginalSoundPlayer.WebDav", source.CredentialKey)); }
        catch { /* 匿名来源或已被用户删除的凭据不阻止移除索引。 */ }
        if (_library is not null) await _library.RefreshSongsSourceAsync();
        SourcesChanged?.Invoke();
    }
    private async Task ScanCoreAsync(WebDavSource source, CancellationToken token)
    {
        int found = 0, tagged = 0;
        try
        {
            var connection = Connect(source);
            await database.RetryDeferredRemoteMetadataAsync(source.Id).ConfigureAwait(false);
            string run = Guid.NewGuid().ToString("N");
            var directories = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (string root in source.Roots.Split('\n', StringSplitOptions.RemoveEmptyEntries)) directories.Enqueue(WebDavTransport.Resolve(connection, root).AbsolutePath);
            if (directories.Count == 0) directories.Enqueue(connection.Root.AbsolutePath);
            Publish(new(source.Id, "Scanning", found, tagged));
            while (directories.TryDequeue(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(directory)) continue;
                var batch = new List<WebDavEntry>(64);
                await foreach (var entry in transport.ListAsync(connection, directory, token).ConfigureAwait(false))
                {
                    if (entry.IsDirectory) { directories.Enqueue(entry.Href); continue; }
                    if (!AudioExtensions.Contains(Path.GetExtension(entry.Name))) continue;
                    batch.Add(entry);
                    found++;
                    if (batch.Count < 64) continue;
                    await PublishBatchAsync(await database.CommitRemoteEntriesAsync(source, directory, run, batch)).ConfigureAwait(false);
                    batch.Clear();
                    Publish(new(source.Id, "Scanning", found, tagged));
                }
                if (batch.Count != 0) await PublishBatchAsync(await database.CommitRemoteEntriesAsync(source, directory, run, batch)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await database.MarkRemoteDirectoryScannedAsync(source.Id, directory, run).ConfigureAwait(false);
                Publish(new(source.Id, "Scanning", found, tagged));
            }
            source.LastScanUtc = DateTime.UtcNow;
            await database.MarkRemoteSourceScannedAsync(source.Id, run).ConfigureAwait(false);
            await database.SaveWebDavSourceAsync(source).ConfigureAwait(false);
            if (_library is not null) await _library.RefreshSongsSourceAsync(token).ConfigureAwait(false);
            if (source.ReadMetadata)
            {
                int after = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var pending = await database.GetPendingRemoteMetadataAsync(source.Id, after).ConfigureAwait(false);
                    if (pending.Count == 0) break;
                    var changed = new List<Music>(pending.Count);
                    foreach (var remote in pending)
                    {
                        token.ThrowIfCancellationRequested();
                        after = remote.MusicId;
                        RemoteMetadata? metadata = null;
                        string status = "Complete";
                        await _metadataSlots.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            using var input = new HttpRangeReadStream(transport, connection, ToEntry(remote), token);
                            metadata = RemoteMetadataProbe.ReadMetadata(input, Path.GetExtension(remote.Href), token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (WebDavException ex) when (ex.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) { throw; }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException)
                        { status = "Deferred"; }
                        finally { _metadataSlots.Release(); }
                        var music = await database.CommitRemoteMetadataAsync(remote, metadata, status).ConfigureAwait(false);
                        if (music is not null) changed.Add(music);
                        tagged++;
                        if (changed.Count >= 16) { await PublishBatchAsync(changed).ConfigureAwait(false); changed.Clear(); }
                        Publish(new(source.Id, "Metadata", found, tagged));
                    }
                    if (changed.Count != 0) await PublishBatchAsync(changed).ConfigureAwait(false);
                }
            }
            Publish(new(source.Id, "Completed", found, tagged));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Publish(new(source.Id, "Cancelled", found, tagged)); }
        catch (Exception ex)
        {
            // HTTP 异常可能携带供应商签名 URL，只记录固定错误类型。
            string error = ex is WebDavException dav ? dav.Code : token.IsCancellationRequested ? "Cancelled" : "ConnectionFailed";
            logger.LogWarning("WebDAV source {SourceId}: {Error}", source.Id, error);
            Publish(new(source.Id, "Failed", found, tagged, error));
        }
    }
    public static WebDavEntry ToEntry(RemoteTrack track) => new(track.Href, Uri.UnescapeDataString(Path.GetFileName(track.Href)), false,
        track.Length, track.ETag, DateTimeOffset.TryParse(track.Modified, out var date) ? date : null);

    private Task PublishBatchAsync(IReadOnlyList<Music> batch) => App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
    {
        if (_stop.IsCancellationRequested || _library is null) return;
        List<Music> added = [];
        foreach (var item in batch)
        {
            item.IsRemoteOffline = IsSourceOffline(item.SourceId);
            var current = _library.FindById(item.Id);
            if (current is null) { added.Add(item); continue; }
            current.IsRemoteOffline = item.IsRemoteOffline;
            current.Title = item.Title; current.Author = item.Author; current.Album = item.Album;
            current.TrackNumber = item.TrackNumber; current.DiskNumber = item.DiskNumber; current.Year = item.Year;
            current.BitDepth = item.BitDepth; current.BitRate = item.BitRate; current.SampleRate = item.SampleRate;
            current.Channel = item.Channel; current.Duration = item.Duration; current.ImageHash = item.ImageHash;
            current.Extension = item.Extension;
        }
        if (added.Count != 0) _library.AppendSongsBatch(added);
        _library.NotifySongsSourceChanged();
    });

    public async Task<(WebDavSource Source, RemoteTrack Track)> ResolveAsync(Music music)
    {
        var track = await database.GetRemoteTrackAsync(music.Id) ?? throw new WebDavException("ResourceMissing");
        foreach (var source in await database.GetWebDavSourcesAsync())
            if (source.Id == track.SourceId && source.Enabled) return (source, track);
        throw new WebDavException("SourceUnavailable");
    }

    public async Task<byte[]> ReadCoverAsync(Music music, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, token);
        await _coverSlot.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var (source, track) = await ResolveAsync(music).ConfigureAwait(false);
            string cacheRoot = AppSettings.MusicCoverCache;
            string folder = WebDavCachePaths.Covers(cacheRoot);
            string file = WebDavCachePaths.Cover(cacheRoot, music.ImageHash);
            bool dsf = music.Extension.Equals(".dsf", StringComparison.OrdinalIgnoreCase);
            long cachedLength = File.Exists(file) ? new FileInfo(file).Length : -1;
            if (cachedLength is >= 0 and <= 8 * 1024 * 1024 &&
                (cachedLength > 0 || !dsf || _checkedDsfCoverMisses.Contains(music.ImageHash)))
                return await File.ReadAllBytesAsync(file, linked.Token).ConfigureAwait(false);
            var connection = Connect(source);
            byte[] bytes = await Task.Run(() =>
            {
                using var input = new HttpRangeReadStream(transport, connection, ToEntry(track), linked.Token, 8 * 1024 * 1024, 64, 20);
                return AudioCoverReader.ReadCover(input, music.Extension);
            }, linked.Token).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                await foreach (var entry in transport.ListAsync(connection, track.ParentHref, linked.Token).ConfigureAwait(false))
                {
                    if (!entry.Name.Equals("cover.jpg", StringComparison.OrdinalIgnoreCase) && !entry.Name.Equals("folder.jpg", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Length <= 0 || entry.Length > 8 * 1024 * 1024) continue;
                    await using var response = await transport.OpenAsync(connection, entry.Href, null, null, false, linked.Token).ConfigureAwait(false);
                    bytes = new byte[(int)entry.Length];
                    linked.CancelAfter(TimeSpan.FromSeconds(15));
                    await response.Stream.ReadExactlyAsync(bytes, linked.Token).ConfigureAwait(false);
                    break;
                }
            }
            try
            {
                // 与详情页按同一个 ImageHash 读写同一文件，发布完整文件后才返回字节。
                // 空文件记录该版本没有封面，避免每次展示都重复发起网络请求。
                await PlaybackCoverCache.StoreAsync(file, bytes, linked.Token).ConfigureAwait(false);
                TrimCovers(folder, file);
                if (dsf && bytes.Length == 0)
                {
                    if (_checkedDsfCoverMisses.Count >= 256) _checkedDsfCoverMisses.Clear();
                    _checkedDsfCoverMisses.Add(music.ImageHash);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogWarning(ex, "写入 WebDAV 封面缓存失败"); }
            return bytes;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException) { return []; }
        finally { _coverSlot.Release(); }
    }
    private static void TrimCovers(string folder, string currentFile)
    {
        var files = new DirectoryInfo(folder).GetFiles("*_raw.bin");
        Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        long size = 0;
        foreach (var file in files) size += file.Length;
        foreach (var file in files)
        {
            if (size <= 256L * 1024 * 1024) break;
            if (file.FullName.Equals(currentFile, StringComparison.OrdinalIgnoreCase)) continue;
            long bytes = file.Length;
            file.Delete();
            size -= bytes;
        }
    }

    /// <summary>与原图读写互斥，只清封面，不影响音频租约。</summary>
    public async Task ClearCoverCacheAsync(string cacheRoot)
    {
        await _coverSlot.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                string folder = WebDavCachePaths.Covers(cacheRoot);
                if (!Directory.Exists(folder)) return;
                foreach (string file in Directory.EnumerateFiles(folder, "*_raw.bin"))
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { logger.LogWarning(ex, "清理 WebDAV 封面缓存失败"); }
                }
            }, _stop.Token).ConfigureAwait(false);
        }
        finally { _coverSlot.Release(); }
    }
    public async Task<string> ReadLyricsAsync(Music music, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, token);
        await _lyricsSlot.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var (source, track) = await ResolveAsync(music).ConfigureAwait(false);
            var connection = Connect(source);
            string name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(track.Href)) + ".lrc";
            linked.CancelAfter(TimeSpan.FromSeconds(15));
            await foreach (var entry in transport.ListAsync(connection, track.ParentHref, linked.Token).ConfigureAwait(false))
            {
                if (entry.IsDirectory || !entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || entry.Length is <= 0 or > 512 * 1024) continue;
                await using var response = await transport.OpenAsync(connection, entry.Href, null, null, false, linked.Token).ConfigureAwait(false);
                byte[] bytes = new byte[(int)entry.Length];
                await response.Stream.ReadExactlyAsync(bytes, linked.Token).ConfigureAwait(false);
                using var input = new MemoryStream(bytes, false);
                using var reader = new StreamReader(input, Encoding.UTF8, true);
                return await reader.ReadToEndAsync(linked.Token).ConfigureAwait(false);
            }
            return "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _stop.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or OperationCanceledException) { return ""; }
        finally { _lyricsSlot.Release(); }
    }
    public async Task ApplyCacheSettingsAsync(WebDavCacheSettings settings, bool save = true)
    {
        settings.LimitGiB = Math.Clamp(settings.LimitGiB, 1, 1024);
        _cacheSettings = settings;
        ConfigureCache();
        if (save) await database.SaveWebDavCacheSettingsAsync(settings);
    }
    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_library is not null) _library.State.Preferences.PropertyChanged -= OnPreferencesChanged;
        Task[] scans;
        Task[] probes;
        Task? availabilityLoop;
        lock (_gate)
        {
            scans = new Task[_scans.Count];
            int i = 0;
            foreach (var scan in _scans.Values) scans[i++] = scan.Work;
            probes = [.. _probes.Values];
            availabilityLoop = _availabilityLoop;
        }
        if (availabilityLoop is not null) await availabilityLoop;
        await Task.WhenAll(probes);
        await Task.WhenAll(scans);
        await _coverSlot.WaitAsync();
        _coverSlot.Release();
        await _lyricsSlot.WaitAsync();
        _lyricsSlot.Release();
        lock (_gate)
        {
            foreach (var scan in _scans.Values) scan.Cancel.Dispose();
            _scans.Clear();
            _probes.Clear();
            _availabilityLoop = null;
        }
        StatusChanged = null;
        SourceAvailabilityChanged = null;
        SourcesChanged = null;
    }
}
