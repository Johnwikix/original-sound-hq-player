using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace MusicMetadataUiRegression;

public sealed partial class MetadataPanel : UserControl
{
    public Music MusicDetail { get; }
    public MusicMetadataEditorViewModel MetadataEditor { get; }
    public MetadataPanel(Music source)
    {
        MusicDetail = source;
        MetadataEditor = new(source);
        InitializeComponent();
    }
}
