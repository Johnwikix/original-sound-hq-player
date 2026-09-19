using System;
using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;

namespace AnimatedTextRegression;

public sealed partial class LayoutRegressionPage : Page
{
    public LayoutViewModel ViewModel { get; }
    public static double GetAnimatedLineHeight(double fontSize) => Math.Ceiling(fontSize * 1.4);
    public AnimatedTextBlock TitleControl => TitleText;
    public AnimatedTextBlock InfoControl => InfoText;

    public LayoutRegressionPage()
    {
        InitializeComponent();
        ViewModel = new LayoutViewModel();
        DataContext = this;
        Loaded += (_, _) =>
        {
            var effect = new TextFadeEffect();
            TitleText.TextEffect = effect;
            InfoText.TextEffect = effect;
            ViewModel.ResizeFonts(44, 30);
        };
    }
}

public sealed class LayoutViewModel : INotifyPropertyChanged
{
    public bool Enabled => true;
    public bool HoverEnabled { get; private set; } = true;
    public string Title { get; private set; } = "All In My Head";
    public string Info { get; private set; } = "Album with a very long name\r\nArtist";
    public double TitleFontSize { get; private set; } = 24;
    public double InfoFontSize { get; private set; } = 22;
    public event PropertyChangedEventHandler PropertyChanged;
    public void SetHoverEnabled(bool enabled)
    {
        HoverEnabled = enabled;
        PropertyChanged?.Invoke(this, new(nameof(HoverEnabled)));
    }
    public void ResizeFonts(double title, double info)
    {
        TitleFontSize = title;
        InfoFontSize = info;
        PropertyChanged?.Invoke(this, new(nameof(TitleFontSize)));
        PropertyChanged?.Invoke(this, new(nameof(InfoFontSize)));
    }
}
