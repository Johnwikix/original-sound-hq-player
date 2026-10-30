using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper;

/// <summary>只在当前展示集合中定位，避免更改搜索、排序或播放队列。</summary>
public static class CurrentTrackLookup
{
    public static Music? Find(IList<Music> source, Music? current)
    {
        if (current is null) return null;
        for (int i = 0; i < source.Count; i++)
            if (ReferenceEquals(source[i], current)) return source[i];
        for (int i = 0; i < source.Count; i++)
            if (Matches(source[i], current)) return source[i];
        return null;
    }

    public static PlayListMusicItem? Find(IList<PlayListMusicItem> source, Music? current)
    {
        if (current is null) return null;
        for (int i = 0; i < source.Count; i++)
            if (ReferenceEquals(source[i].Music, current)) return source[i];
        for (int i = 0; i < source.Count; i++)
            if (Matches(source[i].Music, current)) return source[i];
        return null;
    }

    private static bool Matches(Music candidate, Music current)
        => current.Id != 0 ? candidate.Id == current.Id
            : candidate.Id == 0 && !string.IsNullOrEmpty(current.Path)
                && string.Equals(candidate.Path, current.Path, StringComparison.OrdinalIgnoreCase);
}
