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
public sealed partial class WebDavLibraryService(MusicDatabaseService database, WebDavTransport transport,
    RemoteAudioCache cache, ILogger<WebDavLibraryService> logger)
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, (CancellationTokenSource Cancel, Task Work)> _scans = [];
    private readonly SemaphoreSlim _metadataSlots = new(2, 2);
    private readonly SemaphoreSlim _coverSlot = new(1, 1);
    // Older readers cached DSF as coverless. Recheck these misses once per bounded working set.
    // Access is serialized by _coverSlot; never retain artwork or Music instances here.
    private readonly HashSet<string> _checkedDsfCoverMisses = [];
    private readonly SemaphoreSlim _lyricsSlot = new(1, 1);
    private readonly Dictionary<int, WebDavScanStatus> _statuses = [];
    private readonly Dictionary<int, long> _publishedAt = [];
    private readonly object _gate = new();
    private Task? _availabilityLoop;
    private Task _cacheStatusRefresh = Task.CompletedTask;
    private bool _cacheStatusDirty;
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
        cache.ContentsChanged += OnCacheContentsChanged;
        library.SongsSourceChanged += OnCacheContentsChanged;
        library.PropertyChanged += OnLibraryPropertyChanged;
        await ApplyCacheSettingsAsync(await database.GetWebDavCacheSettingsAsync(), false);
        if (!_stop.IsCancellationRequested) library.State.Preferences.PropertyChanged += OnPreferencesChanged;
    }

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_stop.IsCancellationRequested && e.PropertyName == nameof(AppSettings.MusicCoverCache)) ConfigureCache();
    }

    private void ConfigureCache() => cache.Configure(_cacheSettings.Enabled, AppSettings.MusicCoverCache,
        _cacheSettings.LimitGiB * 1024L * 1024 * 1024);

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.CurrentPlayingMusic) or nameof(AppViewModel.CurrentPlayingList))
            OnCacheContentsChanged();
    }

    private void OnCacheContentsChanged()
    {
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_stop.IsCancellationRequested) return;
            _cacheStatusDirty = true;
            if (_cacheStatusRefresh.IsCompleted) _cacheStatusRefresh = RefreshCacheStatusAsync();
        });
    }

    private async Task RefreshCacheStatusAsync()
    {
        await Task.Yield(); // 先登记任务，再合并启动加载、队列恢复和缓存事件。
        while (_cacheStatusDirty && !_stop.IsCancellationRequested)
        {
            _cacheStatusDirty = false;
            try
            {
                var complete = await Task.Run(cache.GetCompleteFiles);
                var cachedIds = new HashSet<int>();
                if (complete.Count != 0)
                {
                    var tracks = await database.GetVisibleRemoteTracksAsync();
                    await Task.Run(() =>
                    {
                        foreach (var track in tracks)
                        {
                            _stop.Token.ThrowIfCancellationRequested();
                            string key = RemoteAudioCache.GetKey($"webdav://{track.SourceId}{track.Href}", ToEntry(track));
                            if (track.Length > 0 && complete.TryGetValue(key, out long length) && length == track.Length)
                                cachedIds.Add(track.MusicId);
                        }
                    });
                }
                if (_stop.IsCancellationRequested) return;
                if (_cacheStatusDirty || _library is null) continue;
                // 所有绑定对象都在 UI 线程发布，队列可包含不同于曲库的 Music 实例。
                foreach (var music in _library.SongsSource) RefreshRuntimeAvailability(music, cachedIds);
                foreach (var music in _library.CurrentPlayingList) RefreshRuntimeAvailability(music, cachedIds);
                if (_library.CurrentPlayingMusic is { } current) RefreshRuntimeAvailability(current, cachedIds);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "刷新 WebDAV 音频缓存状态失败"); }
        }
    }
    private void RefreshRuntimeAvailability(Music music, HashSet<int> cachedIds)
    {
        music.IsRemoteCached = music.IsRemote && cachedIds.Contains(music.Id);
        music.IsRemoteOffline = music.IsRemote && IsSourceOffline(music.SourceId);
    }
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
        int found = 0, tagged = 0, visibleChanges = 0;
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
                    var (committed, adds) = await database.CommitRemoteEntriesAsync(source, directory, run, batch).ConfigureAwait(false);
                    visibleChanges += adds;
                    await PublishBatchAsync(committed).ConfigureAwait(false);
                    batch.Clear();
                    Publish(new(source.Id, "Scanning", found, tagged));
                }
                if (batch.Count != 0)
                {
                    var (committed, adds) = await database.CommitRemoteEntriesAsync(source, directory, run, batch).ConfigureAwait(false);
                    visibleChanges += adds;
                    await PublishBatchAsync(committed).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                visibleChanges += await database.MarkRemoteDirectoryScannedAsync(source.Id, directory, run).ConfigureAwait(false);
                Publish(new(source.Id, "Scanning", found, tagged));
            }
            source.LastScanUtc = DateTime.UtcNow;
            visibleChanges += await database.MarkRemoteSourceScannedAsync(source.Id, run).ConfigureAwait(false);
            await database.SaveWebDavSourceAsync(source).ConfigureAwait(false);
            // 仅有可见增删（新增、复现或转入缺失）时才整库重载收敛排序与移除行；
            // 服务端无变化时不重建列表，避免启动扫描结束时闪一次。
            if (visibleChanges != 0 && _library is not null) await _library.RefreshSongsSourceAsync(token).ConfigureAwait(false);
            if (source.ReadMetadata)
            {
                int after = 0, applied = 0;
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
                        if (music is not null) { changed.Add(music); applied++; }
                        tagged++;
                        if (changed.Count >= 16) { await PublishBatchAsync(changed).ConfigureAwait(false); changed.Clear(); }
                        Publish(new(source.Id, "Metadata", found, tagged));
                    }
                    if (changed.Count != 0) await PublishBatchAsync(changed).ConfigureAwait(false);
                }
                // 元数据就地更新不重排列表；实际写入后收敛一次排序与各页投影。
                if (applied != 0) await App.MainWindow.DispatcherQueue.EnqueueAsync(
                    () => { if (!_stop.IsCancellationRequested) _library?.NotifySongsSourceChanged(); }).ConfigureAwait(false);
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
        if (_stop.IsCancellationRequested || _library is null || batch.Count == 0) return;
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
        // 与本地扫描批次同口径：新增增量进当前列表，就地更新元数据即可；
        // 逐批 NotifySongsSourceChanged 会让曲目列表整表 Reset（扫描期间持续闪烁），
        // 排序与各页投影由扫描结束时的统一刷新收敛。
        if (added.Count != 0) _library.AppendSongsBatch(added);
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
        cache.ContentsChanged -= OnCacheContentsChanged;
        if (_library is not null)
        {
            _library.SongsSourceChanged -= OnCacheContentsChanged;
            _library.PropertyChanged -= OnLibraryPropertyChanged;
        }
        await DrainSourceSavesAsync();
        await _cacheStatusRefresh;
        if (_library is not null) _library.State.Preferences.PropertyChanged -= OnPreferencesChanged;
        Task[] scans;
        Task[] probes;
        Task? availabilityLoop;
        lock (_gate)
        {
            scans = new Task[_scans.Count];
            int i = 0;
            foreach (var scan in _scans.Values) scans[i++] = scan.Work;
            probes = [.. _probeWork];
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
