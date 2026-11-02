using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services.Recommendations;
namespace WinUIMusicPlayer.Services.LastFm;
/// <summary>后台刷新有限社区缓存；首页只读快照，不等待网络。</summary>
public sealed class LastFmRecommendationService(LastFmService account, MusicDatabaseService database, ILogger<LastFmRecommendationService> logger) : ICommunityRecommendationSource
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task _loop = Task.CompletedTask;
    private Task? _stopTask;
    private CommunitySignals _snapshot = CommunitySignals.Empty;
    private long _revision = -1;
    private DateTime _retryAt;
    private bool _started;
    /// <summary>获取当前账户的快照；断开/关闭后立即返回空。</summary>
    public CommunitySignals Snapshot => account.Status is { Enabled: true, IsAuthenticated: true } status && status.Revision == Volatile.Read(ref _revision)
        ? Volatile.Read(ref _snapshot) : CommunitySignals.Empty;
    /// <summary>通知推荐输入变化，订阅方负责UI派发。</summary>
    public event Action? Changed;
    /// <summary>启动独立低频循环。</summary>
    public void Start()
    {
        lock (_stop)
        {
            if (_started || _stop.IsCancellationRequested) return;
            _started = true;
            account.Changed += AccountChanged;
            _loop = Task.Run(LoopAsync);
            Wake();
        }
    }
    private void AccountChanged(LastFmStatus status)
    {
        lock (_gate)
        {
            if (status.Revision == Volatile.Read(ref _revision)) return;
            Volatile.Write(ref _revision, status.Revision);
            Volatile.Write(ref _snapshot, CommunitySignals.Empty);
            _retryAt = default;
        }
        Changed?.Invoke();
        Wake();
    }
    private void Wake()
    {
        if (_stop.IsCancellationRequested) return;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(5), _stop.Token).ConfigureAwait(false);
                var status = account.Status;
                if (!status.Enabled || !status.IsAuthenticated) continue;
                lock (_gate) if (DateTime.UtcNow < _retryAt) continue;
                long revision = status.Revision;
                try
                {
                    var seeds = await database.GetRecommendationSeedsAsync().ConfigureAwait(false);
                    var artists = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    var tracks = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    var requestedArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int rank = 0;
                    foreach (var seed in seeds)
                    {
                        _stop.Token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(seed.Artist) || string.IsNullOrWhiteSpace(seed.Title)) continue;
                        float weight = 1f / (1 + rank++ * .15f);
                        if (requestedArtists.Add(seed.Artist))
                        {
                            foreach (var item in await ReadSimilarAsync(seed.Artist, null, revision, _stop.Token).ConfigureAwait(false))
                                artists[item.Artist] = Math.Max(artists.GetValueOrDefault(item.Artist), item.Match * weight);
                            await Task.Delay(300, _stop.Token).ConfigureAwait(false);
                        }
                        foreach (var item in await ReadSimilarAsync(seed.Artist, seed.Title, revision, _stop.Token).ConfigureAwait(false))
                        {
                            string key = CommunitySignals.TrackKey(item.Artist, item.Title);
                            tracks[key] = Math.Max(tracks.GetValueOrDefault(key), item.Match * weight);
                        }
                        await Task.Delay(300, _stop.Token).ConfigureAwait(false);
                    }
                    lock (_gate)
                    {
                        if (account.Status.Revision != revision || !account.Status.Enabled) continue;
                        Volatile.Write(ref _revision, revision);
                        Volatile.Write(ref _snapshot, new(artists, tracks));
                        _retryAt = DateTime.UtcNow.AddHours(seeds.Count == 0 ? 0 : 6);
                    }
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    lock (_gate) if (account.Status.Revision == revision) _retryAt = DateTime.UtcNow.AddMinutes(5);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug("Last.fm enrichment postponed: {Type}", ex.GetType().Name);
                    lock (_gate) if (account.Status.Revision == revision) _retryAt = DateTime.UtcNow.AddMinutes(30);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task<List<LastFmSimilarity>> ReadSimilarAsync(string artist, string? title, long revision, CancellationToken token)
    {
        try { return await account.GetSimilarAsync(artist, title, revision, token).ConfigureAwait(false); }
        catch (LastFmApiException ex) when (ex.Code is 6 or 7)
        {
            // 单个冷门/未收录种子不应阻断整批有数据的艺术家。
            return [];
        }
    }
    /// <summary>解除账户订阅，取消并等待网络工作完成。</summary>
    public Task StopAsync()
    {
        lock (_stop)
        {
            if (_stopTask is not null) return _stopTask;
            account.Changed -= AccountChanged;
            _stop.Cancel();
            return _stopTask = FinishAsync();
        }
    }
    private async Task FinishAsync()
    {
        await _loop.ConfigureAwait(false);
        Volatile.Write(ref _snapshot, CommunitySignals.Empty);
        Changed = null;
        _wake.Dispose();
        _stop.Dispose();
    }
}
