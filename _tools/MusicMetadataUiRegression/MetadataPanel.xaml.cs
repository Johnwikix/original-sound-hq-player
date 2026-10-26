using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace MusicMetadataUiRegression;

public sealed partial class MetadataPanel : UserControl, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsLoading
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(IsLoading)));
        }
    }
    public Music MusicDetail { get; }
    public MusicMetadataEditorViewModel MetadataEditor { get; }
    public MetadataPanel(Music source, bool isLoading = false)
    {
        MusicDetail = source;
        MetadataEditor = new(source);
        IsLoading = isLoading;
        InitializeComponent();
    }

    private Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private Visibility BoolToNVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
