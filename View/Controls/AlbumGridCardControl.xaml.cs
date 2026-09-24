using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.View.Controls;

public sealed partial class AlbumGridCardControl : UserControl
{
    public static readonly DependencyProperty MusicProperty = DependencyProperty.Register(
        nameof(Music), typeof(Music), typeof(AlbumGridCardControl), new PropertyMetadata(null));

    public Music? Music
    {
        get => (Music?)GetValue(MusicProperty);
        set => SetValue(MusicProperty, value);
    }

    public AlbumGridCardControl() => InitializeComponent();
}
