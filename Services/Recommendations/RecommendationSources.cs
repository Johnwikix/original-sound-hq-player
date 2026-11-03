using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model.Recommendations;
namespace WinUIMusicPlayer.Services.Recommendations;
/// <summary>首页消费已提交本地特征，不拥有分析调度器。</summary>
public interface IAudioRecommendationSource
{
    event Action? ResultsChanged;
    Task<Dictionary<int, float>> GetScoresAsync(IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history, CancellationToken token);
}
/// <summary>首页只读取社区快照，不发起网络查询。</summary>
public interface ICommunityRecommendationSource
{
    event Action? Changed;
    CommunitySignals Snapshot { get; }
}
