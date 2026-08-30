using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词渲染器抽象。宿主窗口只负责窗口生命周期/锁定/拖动/渲染器选择，
    /// 把数据总线（UILyricsBus / TimeProgressBus / OffsetMsBus / IsPlayingBus）的快照与
    /// 独立样式（DesktopLyricsStyle）推送给渲染器。
    /// 现有两个实现：<see cref="TextBlockLyricsRenderer"/>（文本描边，逐字效果关闭时）与
    /// <see cref="CanvasLyricsRenderer"/>（Win2D 逐字扫光，直接组装库内部件的薄宿主），
    /// 窗口按 ViewModel.IsKaraokeEnabled 选择并支持热切换。
    /// </summary>
    public interface IDesktopLyricsRenderer : IDisposable
    {
        UIElement Content { get; }

        void SetStyle(DesktopLyricsStyle style);

        void SetLyrics(IList<LyricLine>? lyrics);

        void SetPlaybackTime(long totalMs);

        void SetOffset(double offsetMs);

        /// <summary>逐字渲染器用它做时钟暂停/恢复；文本渲染器无需处理。</summary>
        void SetIsPlaying(bool isPlaying);
    }
}
