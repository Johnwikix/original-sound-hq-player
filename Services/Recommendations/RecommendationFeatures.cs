using System;
using WinUIMusicPlayer.Model.Recommendations;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>选择独立召回源，供离线对照与完整管线共用相同栏目分配。</summary>
[Flags]
internal enum RecommendationSources { Metadata = 1, Audio = 2, Community = 4, All = Metadata | Audio | Community }

/// <summary>保存单批次规范化元数据，由推荐构建过程拥有。</summary>
internal readonly record struct RecommendationFeatures(RecommendationTrack Track, string[] Artists,
    string[] Genres, string AlbumKey, string RecordingKey, bool IsCollected = false)
{
    /// <summary>收藏/歌单及同名副本保留候选资格，但推荐排序权重降低90%；不改变训练偏好或常听。</summary>
    internal double RecommendationWeight => IsCollected ? .1 : 1;
}

/// <summary>保存来源评分或融合后的排序分；TieBreaker仅用于同分排序。</summary>
internal readonly record struct RecommendationCandidate(RecommendationFeatures Features, double Score,
    double Affinity, RecommendationReason Reason, string Detail, double TieBreaker = 0);
