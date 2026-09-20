using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
namespace WinUIMusicPlayer.State;

/// <summary>当前歌曲的共享展示数据；解析、图像解码与资源收尾由展示服务拥有。</summary>
public sealed class PlaybackPresentationState : ObservableObject
{
    public List<LyricLine> UILyrics { get; set => SetProperty(ref field, value); } = [];
    public string MusicInfo { get; set => SetProperty(ref field, value); }
    public string LyricPageBackgroundHash { get; set => SetProperty(ref field, value); } = "";
    public AnimatedWin2dControls.Impressionist.PaletteResult? LyricPagePalette { get; set => SetProperty(ref field, value); }
    public AnimatedWin2dControls.Impressionist.ArtworkPixelData? LyricPageArtwork { get; set => SetProperty(ref field, value); }
    public int LastLyricIndex { get; set; } = -1;
    public TimeSpan LyricsDurationTime { get; set; } = TimeSpan.Zero;
}
