using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services;

public sealed partial class WebDavLibraryService
{
    private readonly Dictionary<int, (long Version, Task Work)> _probes = [];
    private readonly HashSet<Task> _probeWork = [];
    private readonly WebDavAvailability _availability = new();
    public bool IsSourceOffline(int sourceId) => _availability.IsOffline(sourceId);

    public void ReportSourceFailure(int sourceId)
    {
        _availability.ReportFailure(sourceId);
        PublishAvailability(sourceId);
    }

    /// <summary>磁盘缓存优先；绑定的缓存图标只是快照，选曲时核对当前版本的实际完整文件。</summary>
    public async Task<bool> CanPlayAsync(Music music, bool retryImmediately, CancellationToken token)
    {
        var (source, track) = await ResolveAsync(music).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        bool cached = await Task.Run(() => cache.HasCompleteFile(music.Path, ToEntry(track)), token).ConfigureAwait(false);
        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            if (!token.IsCancellationRequested && !_stop.IsCancellationRequested)
            {
                music.IsRemoteCached = cached;
                music.IsRemoteOffline = IsSourceOffline(source.Id);
            }
        });
        token.ThrowIfCancellationRequested();
        return cached || await EnsureAvailableAsync(source, token, retryImmediately).ConfigureAwait(false);
    }

    /// <summary>对单个来源执行一次轻量在线检查；不会扫描目录内容。</summary>
    public Task ProbeAsync(WebDavSource source, CancellationToken cancellationToken = default, bool retryImmediately = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task work;
        lock (_gate)
        {
            if (!retryImmediately && _availability.ShouldDefer(source.Id)) return Task.CompletedTask;
            long version = _availability.Version(source.Id);
            if (_probes.TryGetValue(source.Id, out var previous) && previous.Version == version && !previous.Work.IsCompleted) work = previous.Work;
            else
            {
                // 共享探活属于服务生命周期；取消上一次选曲只取消该调用方的等待。
                work = Task.Run(() => ProbeCoreAsync(source, version));
                _probes[source.Id] = (version, work);
                _probeWork.Add(work);
                _ = ObserveProbeAsync(work);
            }
        }
        return work.WaitAsync(cancellationToken);
    }

    private async Task ObserveProbeAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        finally { lock (_gate) _probeWork.Remove(work); }
    }

    public async Task<bool> EnsureAvailableAsync(WebDavSource source, CancellationToken cancellationToken = default, bool retryImmediately = false)
    {
        await ProbeAsync(source, cancellationToken, retryImmediately).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _stop.Token.ThrowIfCancellationRequested();
        return !IsSourceOffline(source.Id);
    }

    private async Task ProbeCoreAsync(WebDavSource source, long version)
    {
        try
        {
            await transport.ProbeAsync(Connect(source), _stop.Token).ConfigureAwait(false);
            if (_availability.CompleteProbe(source.Id, version, false)) PublishAvailability(source.Id);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            string error = ex is WebDavException dav ? dav.Code : "ConnectionFailed";
            logger.LogWarning("WebDAV availability probe failed for source {SourceId}: {Error}", source.Id, error);
            if (_availability.CompleteProbe(source.Id, version, true)) PublishAvailability(source.Id);
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
                        if (source.Enabled) probes.Add(ProbeAsync(source, token, retryImmediately: false));
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

    private void PublishAvailability(int sourceId)
    {
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_stop.IsCancellationRequested) return;
            bool offline = IsSourceOffline(sourceId);
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
}
