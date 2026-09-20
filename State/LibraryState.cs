using System.Collections.Generic;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.State;

/// <summary>音乐库的规范集合与版本；后台结果在 UI 线程提交后递增版本。</summary>
public sealed class LibraryState
{
    public List<Music> Songs { get; private set; } = [];
    public long Version { get; private set; }
    public void Replace(List<Music> songs)
    {
        Songs = songs;
        NotifyChanged();
    }
    public void NotifyChanged() => Version++;
}
