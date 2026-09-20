using CommunityToolkit.Mvvm.ComponentModel;
using System;
using Windows.UI;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;

namespace WinUIMusicPlayer.State;

/// <summary>Appearance 领域偏好的可观察状态；不执行 I/O 或启动后台任务。</summary>
public sealed class AppearancePreferencesState : ObservableObject
{
    public bool UseImageDominantTheme { get; set => SetProperty(ref field, value); } = false;
    public bool IsFluidBackgroundEnabled { get; set => SetProperty(ref field, value); }
    public bool IsFogEffectEnabled { get; set => SetProperty(ref field, value); }
    public bool IsSnowEffectEnabled { get; set => SetProperty(ref field, value); }
    public bool IsRaindropEffectEnabled { get; set => SetProperty(ref field, value); }
    public bool EnableLightWave { get; set => SetProperty(ref field, value); } = true;
    public bool IsWin2dAnimatedText { get; set => SetProperty(ref field, value); } = true;
    public EffectComboBoxItem Win2dTextEffectType { get; set => SetProperty(ref field, value); } = new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextDefaultEffect"), Value = AnimatedTextEffect.TextDefaultEffect };
    public bool IsMusicInfoVisible { get; set => SetProperty(ref field, value); } = true;
    public AnimatedWin2dControls.Impressionist.PaletteAlgorithm PaletteAlgorithm { get; set => SetProperty(ref field, value); } = AnimatedWin2dControls.Impressionist.PaletteAlgorithm.KMeansPP;
    public AnimatedWin2dControls.BackgroundShaderMode BackgroundShader { get; set => SetProperty(ref field, value); } = AnimatedWin2dControls.BackgroundShaderMode.FluidBackground;
    public int CoverSize { get; set => SetProperty(ref field, value); } = 0;
    public bool IsCustomAppSize { get; set => SetProperty(ref field, value); } = false;
    public int AppWidth { get; set => SetProperty(ref field, value); } = 1440;
    public int AppHeight { get; set => SetProperty(ref field, value); } = 810;
    public string BackdropType { get; set => SetProperty(ref field, value); } = "TransparentAcrylic";
    public bool IsDarkMode { get; set => SetProperty(ref field, value); } = false;
    public FontInfo FontFamily { get; set => SetProperty(ref field, value); }
    public Color CustomColor { get; set => SetProperty(ref field, value); } = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
    public float CustomOpacity { get; set => SetProperty(ref field, value); } = 50f;
    public bool IsUpdateBackDrop { get; set => SetProperty(ref field, value); } = false;
    public TextAlignment PlayingDetailAlignment { get; set => SetProperty(ref field, value); } = TextAlignment.Left;
    public bool UsePlayingDetailAlignmentInPortrait { get; set => SetProperty(ref field, value); } = false;
    public bool IsPortraitLayout { get; set => SetProperty(ref field, value); }
}
