using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

internal sealed class WatchedFolderChanges
{
    public int Count => Files.Count + Directories.Count;
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
    // A case-only rename needs both spellings: SQLite NOCASE only folds ASCII.
    public Dictionary<string, string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddDirectory(string current, string? previous)
    {
        if (previous is not null) Directories.TryAdd(previous, previous);
        Directories[current] = current;
    }

    public void Append(WatchedFolderChanges newer)
    {
        Files.UnionWith(newer.Files);
        foreach (var (original, current) in newer.Directories) AddDirectory(current, original);
    }
}

/// <summary>独立的文件监视生命周期。单个消费者合并事件，扫描期间的变化留到下一轮。</summary>
public sealed class LibraryWatcherService(MusicDatabaseService database, AppViewModel state, ILogger<LibraryWatcherService> logger)
{
    private const int MaxPendingPathsPerRoot = 256;
    private readonly HashSet<FileSystemWatcher> _watchers = [];
    private readonly object _pendingLock = new();
    // A null entry asks for a root reconciliation after a buffer overflow or a large burst.
    private Dictionary<string, WatchedFolderChanges?> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _startupTask;
    private Task? _shutdownTask;
    private volatile bool _stopped;

    /// <summary>由 UI 线程启动；设置变化与关闭通过同一门串行管理原生资源。</summary>
    public Task StartAsync()
    {
        if (_stopped) return Task.CompletedTask;
        if (_startupTask is not null) return _startupTask;
        state.PropertyChanged += SettingsChanged;
        database.LocalFoldersChanged += LocalFoldersChanged;
        return _startupTask = ReconcileAsync();
    }

    private async void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppViewModel.IsFolderWatchEnabled)) return;
        try { await ReconcileAsync(); }
        catch (Exception ex) { logger.LogError(ex, "应用目录监视设置失败"); }
    }

    private async void LocalFoldersChanged()
    {
        try { await ReconcileAsync(reloadRoots: true); }
        catch (Exception ex) { logger.LogError(ex, "更新监视目录失败"); }
    }

    private async Task ReconcileAsync(bool reloadRoots = false)
    {
        await _transition.WaitAsync();
        try
        {
            if (_stopped || !state.IsFolderWatchEnabled)
            {
                await StopWatchingAsync();
                return;
            }
            if (_stop is not null && !reloadRoots) return;
            _stop ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _stop.Token;
            try
            {
                var folders = await database.GetFolders().WaitAsync(token);
                // 等待数据库期间设置和退出状态可能变化，禁止迟到的初始化重建资源。
                if (_stopped || !state.IsFolderWatchEnabled)
                {
                    await StopWatchingAsync();
                    return;
                }
                var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in folders)
                    if (!folder.IsExternalImport && !string.IsNullOrEmpty(folder.Path)) desired.Add(folder.Path);
                FileSystemWatcher[] removed;
                lock (_pendingLock)
                {
                    removed = _watchers.Where(watcher => !desired.Contains(watcher.Path)).ToArray();
                    foreach (var watcher in removed) _watchers.Remove(watcher);
                    foreach (string root in _pending.Keys.Where(root => !desired.Contains(root)).ToArray())
                        _pending.Remove(root);
                    // Keep live watchers and their pending events while the roots change.
                    foreach (var watcher in _watchers) desired.Remove(watcher.Path);
                }
                foreach (var watcher in removed) watcher.Dispose();
                foreach (string root in desired)
                {
                    token.ThrowIfCancellationRequested();
                    Watch(root, NotifyFilters.FileName | NotifyFilters.LastWrite);
                    Watch(root, NotifyFilters.DirectoryName);
                }
                _worker ??= Task.Run(() => RunAsync(token));
                lock (_pendingLock)
                    if (_pending.Count > 0) _changes.Writer.TryWrite(true);
            }
            catch (OperationCanceledException) when (_stopped)
            {
                await StopWatchingAsync();
            }
            catch
            {
                await StopWatchingAsync();
                throw;
            }
        }
        finally { _transition.Release(); }
    }
    private void Watch(string root, NotifyFilters filter)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = filter };
            watcher.Changed += Changed;
            watcher.Deleted += Changed;
            watcher.Created += Changed;
            watcher.Renamed += Changed;
            watcher.Error += WatcherError;
            lock (_pendingLock) _watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            if (watcher is not null)
            {
                lock (_pendingLock) _watchers.Remove(watcher);
                watcher.Dispose();
            }
            logger.LogWarning(ex, "无法监视音乐目录 {Path}", root);
        }
    }

    private void WatcherError(object sender, ErrorEventArgs e)
    {
        logger.LogWarning(e.GetException(), "文件监视器丢失事件，安排重新扫描");
        QueueChange((FileSystemWatcher)sender, null);
    }
    private void Changed(object sender, FileSystemEventArgs e)
    {
        var watcher = (FileSystemWatcher)sender;
        string? oldPath = (e as RenamedEventArgs)?.OldFullPath;
        if (watcher.NotifyFilter == NotifyFilters.DirectoryName)
            QueueChange(watcher, e.FullPath, oldPath);
        else
        {
            string? path = RelevantFile(e.FullPath);
            oldPath = RelevantFile(oldPath);
            if (path is not null || oldPath is not null) QueueChange(watcher, path ?? oldPath, oldPath);
        }

        static string? RelevantFile(string? path) =>
            path is not null && ToolUtils.IsMusicFile(Path.GetExtension(path)) && !AudioFileWriteGate.IsOwnWriteEvent(path)
                ? path : null;
    }

    private void QueueChange(FileSystemWatcher watcher, string? path, string? oldPath = null)
    {
        lock (_pendingLock)
        {
            if (_stopped || !state.IsFolderWatchEnabled || !_watchers.Contains(watcher)) return;
            string root = watcher.Path;
            if (path is null)
                _pending[root] = null;
            else
            {
                if (!_pending.TryGetValue(root, out var paths))
                    _pending[root] = paths = new();
                if (paths is null) return;
                if (watcher.NotifyFilter == NotifyFilters.DirectoryName)
                    paths.AddDirectory(path, oldPath);
                else
                {
                    paths.Files.Add(path);
                    if (oldPath is not null) paths.Files.Add(oldPath);
                }
                if (paths.Count > MaxPendingPathsPerRoot) _pending[root] = null;
            }
            _changes.Writer.TryWrite(true);
        }
    }
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(token))
            {
                await Task.Delay(1000, token);
                Dictionary<string, WatchedFolderChanges?> changes;
                lock (_pendingLock)
                {
                    if (token.IsCancellationRequested) return;
                    if (!state.IsFolderWatchEnabled) continue;
                    while (_changes.Reader.TryRead(out _)) { }
                    changes = _pending;
                    _pending = new(StringComparer.OrdinalIgnoreCase);
                }
                if (changes.Count == 0) continue;
                try
                {
                    await AutoRescanService.AutoScan(database, changes, token);
                    token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    RestoreInterrupted(changes);
                    break;
                }
                catch (Exception ex) { logger.LogError(ex, "文件变化后重新扫描失败"); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void RestoreInterrupted(Dictionary<string, WatchedFolderChanges?> changes)
    {
        lock (_pendingLock)
        {
            if (_stopped) return;
            foreach (var (root, interrupted) in changes)
            {
                if (!_pending.TryGetValue(root, out var newer)) _pending[root] = interrupted;
                else if (interrupted is null || newer is null) _pending[root] = null;
                else
                {
                    interrupted.Append(newer);
                    _pending[root] = interrupted.Count > MaxPendingPathsPerRoot ? null : interrupted;
                }
            }
        }
    }

    /// <summary>终止服务；后续设置变化不能重新启动。UI 线程调用。</summary>
    public Task StopAsync()
    {
        if (_shutdownTask is not null) return _shutdownTask;
        _stopped = true;
        state.PropertyChanged -= SettingsChanged;
        database.LocalFoldersChanged -= LocalFoldersChanged;
        _lifetime.Cancel();
        return _shutdownTask = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        await _transition.WaitAsync();
        try { await StopWatchingAsync(); }
        finally
        {
            _lifetime.Dispose();
            _transition.Release();
        }
    }

    private async Task StopWatchingAsync()
    {
        _stop?.Cancel();
        FileSystemWatcher[] watchers;
        lock (_pendingLock)
        {
            if (_stopped) _pending.Clear();
            watchers = [.. _watchers];
            _watchers.Clear();
        }
        foreach (var watcher in watchers) watcher.Dispose();
        try
        {
            if (_worker is not null) await _worker;
        }
        finally
        {
            _stop?.Dispose();
            _stop = null;
            _worker = null;
            while (_changes.Reader.TryRead(out _)) { }
        }
    }
}
