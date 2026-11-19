using AnimatedWin2dControls.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI;

namespace WinUIMusicPlayer.State;

public sealed class PlaybackSpectrumPreferencesState : ObservableObject
{
    private PlaybackSpectrumOptions _options = new();

    public PlaybackSpectrumOptions Options
    {
        get => _options;
        set
        {
            if (SetProperty(ref _options, value.Normalize()))
                OnPropertyChanged(string.Empty);
        }
    }

    public bool Enabled { get => _options.Enabled; set => Update(_options with { Enabled = value }); }
    public int Placement { get => (int)_options.Placement; set => Update(_options with { Placement = (SpectrumPlacement)value }); }
    public int Style { get => (int)_options.Style; set => Update(_options with { Style = (SpectrumStyle)value }); }
    public int ColorMode { get => (int)_options.ColorMode; set => Update(_options with { ColorMode = (SpectrumColorMode)value }); }
    public bool IsCustomColor => _options.ColorMode == SpectrumColorMode.Custom;

    public Color CustomColor
    {
        get
        {
            uint argb = _options.CustomArgb;
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        set => Update(_options with { CustomArgb = (uint)value.A << 24 | (uint)value.R << 16 | (uint)value.G << 8 | value.B });
    }

    public int Opacity { get => _options.Opacity; set => Update(_options with { Opacity = value }); }
    public int Count { get => _options.Count; set => Update(_options with { Count = value }); }
    public int Sensitivity { get => _options.Sensitivity; set => Update(_options with { Sensitivity = value }); }
    public int DelayMs { get => _options.DelayMs; set => Update(_options with { DelayMs = value }); }
    public bool Breathing { get => _options.Breathing; set => Update(_options with { Breathing = value }); }
    public int BreathingIntensity { get => _options.BreathingIntensity; set => Update(_options with { BreathingIntensity = value }); }
    public bool Glow { get => _options.Glow; set => Update(_options with { Glow = value }); }

    private void Update(PlaybackSpectrumOptions value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref _options, value.Normalize(), propertyName)) return;
        if (propertyName == nameof(ColorMode)) OnPropertyChanged(nameof(IsCustomColor));
        OnPropertyChanged(nameof(Options));
    }
}
