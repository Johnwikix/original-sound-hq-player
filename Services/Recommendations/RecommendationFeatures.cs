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
    /// <summary>标题含Instrumental（忽略大小写）的版本降低75%展示权重，不改变偏好训练。</summary>
    internal double InstrumentalWeight => Track.Title?.Contains("Instrumental", StringComparison.OrdinalIgnoreCase) == true ? .25 : 1;

    /// <summary>收藏/歌单及同名副本乘0.1，再叠加伴奏版本降权；常听仅使用伴奏版本系数。</summary>
    internal double RecommendationWeight => (IsCollected ? .1 : 1) * InstrumentalWeight;
}

/// <summary>保存来源评分或融合后的排序分；TieBreaker是供同分排序和最终随机重排共用的批次随机值。</summary>
internal readonly record struct RecommendationCandidate(RecommendationFeatures Features, double Score,
    double Affinity, RecommendationReason Reason, string Detail, double TieBreaker = 0);
