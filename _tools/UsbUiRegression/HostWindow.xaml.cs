using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;

namespace UsbUiRegression;

public sealed partial class HostWindow : Window
{
    public AppState State { get; } = new();
    public ObservableCollection<Music> Songs { get; } = [];
    public HostWindow() { InitializeComponent(); Bindings.Initialize(); }
    public bool Loaded => SongsList.IsLoaded;
    public string BoundSummary => ResultBar.Message;
    public bool SummaryVisible => ResultBar.IsOpen;
    public string? BoundState(Music music) => SongsList.ContainerFromItem(music) is DependencyObject row ? Find(row)?.Text : null;

    private static TextBlock? Find(DependencyObject parent)
    {
        if (parent is TextBlock { Name: "StateText" } found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}
