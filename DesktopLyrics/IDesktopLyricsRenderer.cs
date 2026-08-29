using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词渲染器抽象。宿主窗口只负责窗口生命周期/锁定/拖动，
    /// 并把数据总线（UILyricsBus / TimeProgressBus / OffsetMsBus / IsPlayingBus）的快照推送给渲染器；
    /// 样式总线（LyricsFontSizeBus / LyricsSettingsBus）由渲染器自行订阅。
    /// 预留逐字效果接入：未来实现 AdvanceLyricsRenderer 包装 AdvanceLyricsCanvasControl
    /// （该控件 Loaded 后经 LyricsRenderCoordinator 自订阅全部总线，Set* 可留空实现）。
    /// </summary>
    public interface IDesktopLyricsRenderer : IDisposable
    {
        UIElement Content { get; }

        void SetLyrics(IList<LyricLine>? lyrics);

        void SetPlaybackTime(long totalMs);

        void SetOffset(double offsetMs);

        /// <summary>文本渲染器无需处理暂停；为未来逐字渲染的时钟暂停预留。</summary>
        void SetIsPlaying(bool isPlaying);
    }
}
