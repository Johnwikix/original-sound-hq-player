using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Extensions;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.State;

/// <summary>规范队列与当前遍历顺序的唯一所有者。修改只通过队列操作，在 UI 线程发布。</summary>
public sealed class PlaybackQueueState : ObservableObject
{
    public PlaybackQueueState() => Playing = Sequential;
    public BulkObservableCollection<Music> Sequential { get; private set; } = [];
    public BulkObservableCollection<Music> Playing { get; private set; } = [];
    private PlayMode _mode = PlayMode.ListLoop;
    public PlayMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            RebuildOrder();
        }
    }

    public void Replace(BulkObservableCollection<Music>? songs)
    {
        if (ReferenceEquals(Sequential, songs)) return;
        Sequential = songs ?? [];
        OnPropertyChanged(nameof(Sequential));
        RebuildOrder();
    }

    private void RebuildOrder()
    {
        Playing = Mode == PlayMode.RandomLoop ? Sequential.CreateShuffled() : Sequential;
        OnPropertyChanged(nameof(Playing));
    }

    /// <summary>优先对象身份；库外 Id=0 必须比较路径，不能把任意外部文件视为同一曲。</summary>
    public int IndexOf(Music? current)
    {
        if (current is null) return -1;
        for (int i = 0; i < Playing.Count; i++)
            if (ReferenceEquals(Playing[i], current)) return i;
        for (int i = 0; i < Playing.Count; i++)
            if (current.Id != 0 ? Playing[i].Id == current.Id
                : Playing[i].Id == 0 && string.Equals(Playing[i].Path, current.Path, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public void InsertNext(Music? current, IEnumerable<Music> songs)
    {
        List<Music> batch = [];
        foreach (var song in songs) if (song is not null) batch.Add(song);
        if (batch.Count == 0) return;
        int index = IndexOf(current);
        if (ReferenceEquals(Sequential, Playing))
        {
            Playing.InsertRange(index < 0 ? Playing.Count : index + 1, batch);
            return;
        }
        // 随机顺序只影响遍历；新增成员必须也进入持久化的规范队列，保留重复条目。
        Sequential.AddRange(batch);
        Playing.InsertRange(index < 0 ? Playing.Count : index + 1, batch);
    }
}
