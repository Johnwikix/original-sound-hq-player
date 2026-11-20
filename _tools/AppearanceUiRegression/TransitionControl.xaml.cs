using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel.Controls;

namespace AppearanceUiRegression;

public sealed partial class TransitionControl : UserControl
{
    public PlaybackTransitionsViewModel PlaybackTransitions { get; }
    public bool IsFadeEnabled { get; set; }

    public TransitionControl(PlaybackTransitionsViewModel transitions)
    {
        PlaybackTransitions = transitions;
        InitializeComponent();
    }
}
