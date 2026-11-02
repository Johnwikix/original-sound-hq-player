using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    /// <summary>读取首页所需的有限窗口收听摘要，不触发曲库扫描。</summary>
    public async Task<List<HomeListening>> GetHomeListeningAsync(DateTime nowUtc, CancellationToken token)
    {
        await WhenInitialized.WaitAsync(token);
        return await HomeListeningQuery.ReadAsync(_dbConnection, nowUtc, PlaybackStatsService.QualifiedRatio, token);
    }
    /// <summary>只读取最多八条本地偏好种子，供后台社区增强使用。</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(RecommendationSeed))]
    public async Task<List<RecommendationSeed>> GetRecommendationSeedsAsync()
    {
        await WhenInitialized;
        return await _dbConnection.QueryAsync<RecommendationSeed>(
            "SELECT Title, Author AS Artist FROM Music WHERE IsFavorite=1 OR PlayCount>0 ORDER BY IsFavorite DESC,PlayCount DESC,Id LIMIT 8");
    }
    /// <summary>有界种子的后台值快照。</summary>
    public sealed class RecommendationSeed
    {
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
    }
}
