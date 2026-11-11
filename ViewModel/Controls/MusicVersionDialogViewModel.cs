using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>拥有候选选择／合并预览的临时输入。</summary>
public sealed class MusicVersionDialogViewModel : ObservableObject
{
    /// <summary>初始化预览；所有候选均已由服务检查。</summary>
    public MusicVersionDialogViewModel(IReadOnlyList<Music> versions, bool merging, string description)
    {
        IsMerging = merging;
        Description = description;
        foreach (var version in versions) Versions.Add(new(version));
        if (Versions.Count > 0) Selected = Versions[0];
    }
    /// <summary>获取候选文件。</summary>
    public ObservableCollection<MusicVersionItem> Versions { get; } = [];
    /// <summary>获取或设置选择的候选。</summary>
    public MusicVersionItem? Selected { get; set => SetProperty(ref field, value); }
    /// <summary>获取是否为最终合并预览。</summary>
    public bool IsMerging { get; }
    /// <summary>获取完整影响说明。</summary>
    public string Description { get; }
    /// <summary>获取或设置默认歌词来源选项。</summary>
    public int LyricsChoiceIndex { get; set; }
    /// <summary>获取预览中当前歌曲的歌词全文。</summary>
    public string TargetLyrics { get; init; } = "";
    /// <summary>获取预览中候选歌曲的歌词全文。</summary>
    public string OtherLyrics { get; init; } = "";
    /// <summary>获取预览中版本、收藏来源和歌单出现项的计数。</summary>
    public string Impact { get; init; } = "";
    /// <summary>获取或设置是否折叠歌单重复项；默认关闭。</summary>
    public bool? CollapseDuplicates { get; set; } = false;
}
