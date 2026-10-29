using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Model.Recommendations;

/// <summary>保存一次推荐计算使用的不可变曲目字段，不向后台传递可变的 Music。</summary>
public sealed record RecommendationTrack(int Id, string[] Artists, string Album, string AlbumArtist,
    string Genre, int Year, bool IsFavorite, bool IsAvailable, int PlayCount);

/// <summary>保存按曲目聚合的有效收听信号；时间均为 UTC ticks。</summary>
public sealed class HomeListening
{
    /// <summary>获取或设置曲目 ID。</summary>
    public int MusicId { get; set; }
    /// <summary>获取或设置按收听比例与时间衰减计算的强度。</summary>
    public double Weight { get; set; }
    /// <summary>获取或设置窗口内有效收听次数。</summary>
    public int Count { get; set; }
    /// <summary>获取或设置最后一次有效收听的 UTC ticks。</summary>
    public long LastPlayedTicks { get; set; }
}

/// <summary>标识推荐理由，展示层负责本地化。</summary>
public enum RecommendationReason { Explore, Artist, Genre, Favorite, Familiar }

/// <summary>保存推荐曲目 ID 与有数据依据的理由。</summary>
public sealed record RecommendedSong(int MusicId, RecommendationReason Reason, string Detail = "");

/// <summary>保存一次完整推荐快照；播放队列不跟随此快照的后续刷新。</summary>
public sealed record HomeRecommendation(IReadOnlyList<RecommendedSong> Songs,
    IReadOnlyList<RecommendedSong> Familiar, IReadOnlyList<RecommendedSong> Discovery, bool IsColdStart);
