using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    private RecommendationStateRepository? _recommendationStateRepository;
    private RecommendationStateRepository RecommendationStateRepository =>
        _recommendationStateRepository ??= new(_dbConnection);

    public async Task<HomeRecommendationState> GetHomeRecommendationStateAsync(DateTime nowUtc, CancellationToken token)
    {
        await WhenInitialized.WaitAsync(token);
        return await RecommendationStateRepository.LoadAsync(nowUtc, token);
    }

    /// <summary>仅在状态成功读取后调用；首页负责串行合并并在退出时等待写入。</summary>
    public Task SaveHomeRecommendationStateAsync(HomeRecommendationState state) =>
        RecommendationStateRepository.SaveAsync(state, DateTime.UtcNow);

    /// <summary>明确偏好已持久化并在UI线程发布；消费者延后到下次推荐应用。</summary>
    public event Action? RecommendationPreferencesChanged;

    private void PublishRecommendationPreferencesChanged()
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null) return;
        if (dispatcher.HasThreadAccess) RecommendationPreferencesChanged?.Invoke();
        else dispatcher.TryEnqueue(() => RecommendationPreferencesChanged?.Invoke());
    }

    /// <summary>只读取用户歌单的去重成员；不读取当前播放队列。</summary>
    public async Task<HashSet<int>> GetRecommendationPlaylistIdsAsync(CancellationToken token)
    {
        await WhenInitialized.WaitAsync(token);
        var ids = await _dbConnection.QueryScalarsAsync<int>(
            "SELECT DISTINCT pm.MusicId FROM PlayListMusic pm JOIN PlayList p ON p.Id=pm.PlayListId WHERE pm.MusicId>0").ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new HashSet<int>(ids);
    }

    /// <summary>读取首页所需的有限窗口收听摘要，不触发曲库扫描。</summary>
    public async Task<List<HomeListening>> GetHomeListeningAsync(DateTime nowUtc, CancellationToken token)
    {
        await WhenInitialized.WaitAsync(token);
        return await HomeListeningQuery.ReadAsync(_dbConnection, nowUtc, PlaybackStatsService.QualifiedRatio, token);
    }
    /// <summary>收藏、用户歌单和有效收听共用正向偏好，选择最多八位不同艺术家。</summary>
    public async Task<List<CommunitySeed>> GetRecommendationSeedsAsync()
    {
        await WhenInitialized;
        var candidates = await CommunitySeedQuery.ReadAsync(_dbConnection, DateTime.UtcNow, PlaybackStatsService.QualifiedRatio, CancellationToken.None).ConfigureAwait(false);
        return CommunitySeedQuery.Select(candidates, ArtistHelper.GetArtistNames);
    }
}
