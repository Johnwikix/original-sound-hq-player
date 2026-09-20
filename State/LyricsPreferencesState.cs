using CommunityToolkit.Mvvm.ComponentModel;
using System;
using Windows.UI;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;

namespace WinUIMusicPlayer.State;

/// <summary>Lyrics 领域偏好的可观察状态；不执行 I/O 或启动后台任务。</summary>
public sealed class LyricsPreferencesState : ObservableObject
{
    public Thickness LyricsMargin { get; set => SetProperty(ref field, value); }
    public bool EnableAdvancedLyricsEffect { get; set => SetProperty(ref field, value); } = true;
    public bool IsAutoLyricsEnabled { get; set => SetProperty(ref field, value); } = true;
    public bool IsCustomLyricsColorEnabled { get; set => SetProperty(ref field, value); } = false;
    public Color LyricsCustomColor { get; set => SetProperty(ref field, value); } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    public double DesktopLyricsFontSize { get; set => SetProperty(ref field, value); } = 36;
    public Color DesktopLyricsColor { get; set => SetProperty(ref field, value); } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    public bool IsDesktopLyricsCustomColorEnabled { get; set => SetProperty(ref field, value); } = false;
    public bool IsDesktopLyricsTranslationEnabled { get; set => SetProperty(ref field, value); } = true;
    public bool IsDesktopLyricsGlowEnabled { get; set => SetProperty(ref field, value); } = true;
    public bool IsDesktopLyricsCharFloatEnabled { get; set => SetProperty(ref field, value); } = true;
    public bool IsDesktopLyricsCharScaleEnabled { get; set => SetProperty(ref field, value); } = true;
    public double DesktopLyricsLongSyllableThreshold { get; set { value = Math.Clamp(value, 0, 5000); SetProperty(ref field, value); } } = 700.0;
    public double DesktopLyricsGlowAmount { get; set { value = Math.Clamp(value, 0, 10); SetProperty(ref field, value); } } = 5.0;
    public double DesktopLyricsCharFloatAmount { get; set { value = Math.Clamp(value, 0, 10); SetProperty(ref field, value); } } = 5.0;
    public double DesktopLyricsCharScaleAmount { get; set { value = Math.Clamp(value, 50, 150); SetProperty(ref field, value); } } = 110.0;
    public int DesktopLyricsFontWeight { get; set => SetProperty(ref field, value); } = 400;
    public double DesktopLyricsShadowAmount { get; set { value = Math.Clamp(value, 0, 100); SetProperty(ref field, value); } } = 50.0;
    public int LyricsFontWeight { get; set => SetProperty(ref field, value); } = 700;
    public Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment LyricsAlignment { get; set => SetProperty(ref field, value); } = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left;
    public bool IsGlobalFontSizeEnabled { get; set => SetProperty(ref field, value); } = false;
    public double GlobalFontSize { get; set => SetProperty(ref field, value); } = 32f;
    public double LyricsFontSize { get; set => SetProperty(ref field, value); } = 32;
    public float LyricsBlurAmount { get; set => SetProperty(ref field, value); } = 5f;
    public double CharFloatAmount { get; set => SetProperty(ref field, value); } = 5.0;
    public double CharScaleAmount { get; set => SetProperty(ref field, value); } = 110.0;
    public double GlowAmount { get; set => SetProperty(ref field, value); } = 5.0;
    public double LongSyllableThreshold { get; set => SetProperty(ref field, value); } = 700.0;
    public double PlayingLineTopOffsetPercent { get; set => SetProperty(ref field, value); } = 40.0;
    public double TranslatedOpacityPercent { get; set => SetProperty(ref field, value); } = 60.0;
    public double UnplayedOpacityPercent { get; set => SetProperty(ref field, value); } = 50.0;
    public double TargetFrameRate { get; set => SetProperty(ref field, value); } = 60.0;
    public EasingType ScrollEasingType { get; set => SetProperty(ref field, value); } = EasingType.FlowWave;
    public EaseMode ScrollEasingMode { get; set => SetProperty(ref field, value); } = EaseMode.FlowWave;
}
