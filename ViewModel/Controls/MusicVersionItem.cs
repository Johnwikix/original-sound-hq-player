using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>提供版本 Pivot 的可编译绑定行，不持有窗口或重复播放状态。</summary>
public sealed class MusicVersionItem : ObservableObject
{
    /// <summary>初始化版本展示快照。</summary>
    public MusicVersionItem(Music music) => Music = music;
    /// <summary>获取具体文件版本。</summary>
    public Music Music { get; }
    /// <summary>缺失文件的整行使用弱化显示，仍可选中查看或移除。</summary>
    public static double RowOpacity(bool isMissing) => isMissing ? 0.45 : 1.0;
    /// <summary>获取版本标题。</summary>
    public string Title => string.IsNullOrEmpty(Music.Extension) ? Music.Title : Music.Extension.ToUpperInvariant();
    /// <summary>获取各版本自己的音频信息。</summary>
    public string TechnicalInfo => string.Create(CultureInfo.CurrentCulture,
        $"{Number(Music.SampleRate)} Hz · {Number(Music.BitDepth)} bit · {Number(Music.BitRate)} kbps · {Duration(Music.Duration)}");
    private static string Number(int value) => value > 0 ? value.ToString("N0", CultureInfo.CurrentCulture) : "—";
    private static string Duration(System.TimeSpan value) => value <= System.TimeSpan.Zero ? "—" : value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : value.ToString(@"mm\:ss", CultureInfo.CurrentCulture);
    /// <summary>获取原文件路径。</summary>
    public string Path => Music.Path;
    /// <summary>获取可用性、首选与播放状态。</summary>
    public string StateText { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>更新派生标记，实际状态仍由歌曲和播放器拥有。</summary>
    public void Refresh(int? preferred, int playingId)
    {
        string status = ToolUtils.GetString(Music.IsMissing ? "SongVersionsMissing" : Music.IsRemoteOffline && !Music.IsRemoteCached
            ? "SongVersionsOffline" : "SongVersionsAvailable");
        if (preferred == Music.Id) status += " · " + ToolUtils.GetString("SongVersionsPreferred");
        if (playingId == Music.Id) status += " · " + ToolUtils.GetString("SongVersionsPlaying");
        if (Music.HasVersionLyrics) status += " · " + ToolUtils.GetString("SongVersionsOwnLyrics");
        StateText = status;
    }
}
