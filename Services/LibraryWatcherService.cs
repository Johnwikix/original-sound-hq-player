using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
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

    public async Task StartAsync()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        foreach (var folder in await database.GetFolders())
        {
            if (token.IsCancellationRequested) return;
            if (string.IsNullOrEmpty(folder.Path)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                watcher.Changed += Changed; watcher.Deleted += Changed;
                watcher.Created += Changed; watcher.Renamed += Changed;
                watcher.Error += WatcherError;
                _watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { logger.LogWarning(ex, "无法监视音乐目录 {Path}", folder.Path); }
        }
        _worker = RunAsync(token);
    }
    private void WatcherError(object sender, ErrorEventArgs e)
    {
        logger.LogWarning(e.GetException(), "文件监视器丢失事件，安排重新扫描");
        if (state.IsFolderWatchEnabled) _changes.Writer.TryWrite(true);
    }
    private void Changed(object sender, FileSystemEventArgs e)
    {
        if (state.IsFolderWatchEnabled && !AudioFileWriteGate.IsOwnWriteEvent(e.FullPath))
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
    public async Task StopAsync()
    {
        if (_stop is null) return;
        _stop.Cancel();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        if (_worker is not null) await _worker;
        _stop.Dispose();
        // 不支持停机后重启；退出路径可重复调用。
        _stop = null;
        _worker = null;
    }
}
