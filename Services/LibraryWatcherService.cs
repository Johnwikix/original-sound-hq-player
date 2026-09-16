using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>独立的文件监视生命周期。单个消费者合并事件，扫描期间的变化留到下一轮。</summary>
public sealed class LibraryWatcherService(MusicDatabaseService database, AppViewModel state, ILogger<LibraryWatcherService> logger)
{
    private readonly List<FileSystemWatcher> _watchers = [];
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
        return _startupTask = ReconcileAsync();
    }

    private async void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppViewModel.IsFolderWatchEnabled)) return;
        try { await ReconcileAsync(); }
        catch (Exception ex) { logger.LogError(ex, "应用目录监视设置失败"); }
    }

    private async Task ReconcileAsync()
    {
        await _transition.WaitAsync();
        try
        {
            if (_stopped || !state.IsFolderWatchEnabled)
            {
                await StopWatchingAsync();
                return;
            }
            if (_stop is not null) return;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
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
                foreach (var folder in folders)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(folder.Path)) continue;
                    FileSystemWatcher? watcher = null;
                    try
                    {
                        watcher = new FileSystemWatcher(folder.Path)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                        };
                        watcher.Changed += Changed;
                        watcher.Deleted += Changed;
                        watcher.Created += Changed;
                        watcher.Renamed += Changed;
                        watcher.Error += WatcherError;
                        watcher.EnableRaisingEvents = true;
                        _watchers.Add(watcher);
                    }
                    catch (Exception ex)
                    {
                        watcher?.Dispose();
                        logger.LogWarning(ex, "无法监视音乐目录 {Path}", folder.Path);
                    }
                }
                _worker = RunAsync(token);
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
    private void WatcherError(object sender, ErrorEventArgs e)
    {
        logger.LogWarning(e.GetException(), "文件监视器丢失事件，安排重新扫描");
        if (!_stopped && state.IsFolderWatchEnabled) _changes.Writer.TryWrite(true);
    }
    private void Changed(object sender, FileSystemEventArgs e)
    {
        if (!_stopped && state.IsFolderWatchEnabled && !AudioFileWriteGate.IsOwnWriteEvent(e.FullPath))
            _changes.Writer.TryWrite(true);
    }
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(token))
            {
                await Task.Delay(1000, token);
                while (_changes.Reader.TryRead(out _)) { }
                if (!state.IsFolderWatchEnabled) continue;
                App.MainWindow.DispatcherQueue.TryEnqueue(() => state.ProcessRingVisibility = Visibility.Visible);
                try { await AutoRescanService.AutoScan(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "文件变化后重新扫描失败"); }
                finally { App.MainWindow.DispatcherQueue.TryEnqueue(() => state.ProcessRingVisibility = Visibility.Collapsed); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    /// <summary>终止服务；后续设置变化不能重新启动。UI 线程调用。</summary>
    public Task StopAsync()
    {
        if (_shutdownTask is not null) return _shutdownTask;
        _stopped = true;
        state.PropertyChanged -= SettingsChanged;
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
        if (_stop is null) return;
        _stop.Cancel();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        try
        {
            if (_worker is not null) await _worker;
        }
        finally
        {
            _stop.Dispose();
            _stop = null;
            _worker = null;
            while (_changes.Reader.TryRead(out _)) { }
        }
    }
}
