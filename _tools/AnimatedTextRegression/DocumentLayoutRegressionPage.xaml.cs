using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;

namespace AnimatedTextRegression;

public sealed partial class DocumentLayoutRegressionPage : Page
{
    public LayoutViewModel ViewModel { get; }
    public AnimatedTextBlock TextControl => CombinedText;

    public static AnimatedTextDocument CreateDocument(string title, string info, double titleSize, double infoSize) => new(
        new AnimatedTextParagraph(title, titleSize, FontWeights.SemiBold, Math.Ceiling(titleSize * 1.4)),
        new AnimatedTextParagraph(info, infoSize, FontWeights.Normal, Math.Ceiling(infoSize * 1.4), 0.8));

    public DocumentLayoutRegressionPage()
    {
        InitializeComponent();
        ViewModel = new LayoutViewModel();
        DataContext = this;
        Loaded += (_, _) =>
        {
            CombinedText.TextEffect = new TextFadeEffect();
            ViewModel.ResizeFonts(44, 30);
        };
    }
}
