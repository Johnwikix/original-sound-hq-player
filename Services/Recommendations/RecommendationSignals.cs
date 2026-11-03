using System;
using System.Collections.Generic;
namespace WinUIMusicPlayer.Services.Recommendations;
/// <summary>不可变发布的有限社区快照；无网络时首页继续使用本地推荐。</summary>
public sealed record CommunitySignals(IReadOnlyDictionary<string, float> Artists, IReadOnlyDictionary<string, float> Tracks)
{
    /// <summary>获取无增强快照。</summary>
    public static CommunitySignals Empty { get; } = new(new Dictionary<string, float>(), new Dictionary<string, float>());
    /// <summary>获取保守的艺术家和曲名匹配键。</summary>
    public static string TrackKey(string artist, string title) => string.Concat(artist.Trim(), "\u001f", title.Trim());
}
