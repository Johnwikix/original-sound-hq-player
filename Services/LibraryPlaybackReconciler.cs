using System.Collections.Generic;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>在 UI 线程同步队列中的库记录，保留当前播放对象与用户的队列顺序。</summary>
internal static class LibraryPlaybackReconciler
{
    internal static void Reconcile(IList<Music> queue, IReadOnlyDictionary<int, Music> songs, Music? current)
    {
        // 反向原地更新；不重建随机队列，也不改变正在解码的歌曲对象。
        for (int i = queue.Count - 1; i >= 0; i--)
        {
            var music = queue[i];
            if (music.Id == current?.Id) continue;
            if (songs.TryGetValue(music.Id, out var updated))
            {
                if (!ReferenceEquals(music, updated)) queue[i] = updated;
            }
            else queue.RemoveAt(i);
        }
    }
}
