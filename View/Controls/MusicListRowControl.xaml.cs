using System;
using DevWinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>歌曲列表行的视觉内容；容器选择、菜单和导航由宿主页负责。</summary>
public sealed partial class MusicListRowControl : UserControl
{
    public static readonly DependencyProperty MusicProperty = DependencyProperty.Register(
        nameof(Music), typeof(Music), typeof(MusicListRowControl), new PropertyMetadata(null, OnMusicChanged));

    public static readonly DependencyProperty ShowTrackNumbersProperty = DependencyProperty.Register(
        nameof(ShowTrackNumbers), typeof(bool), typeof(MusicListRowControl), new PropertyMetadata(false));

    public Music? Music
    {
        get => (Music?)GetValue(MusicProperty);
        set => SetValue(MusicProperty, value);
    }

    public bool ShowTrackNumbers
    {
        get => (bool)GetValue(ShowTrackNumbersProperty);
        set => SetValue(ShowTrackNumbersProperty, value);
    }

    public event EventHandler<Music>? ArtistInvoked;
    public event EventHandler<Music>? AlbumInvoked;

    public MusicListRowControl() => InitializeComponent();

    private GridLength TrackColumnWidth(bool visible) => visible
        ? new GridLength(0.5, GridUnitType.Star) : new GridLength(0);

    private static void OnMusicChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((MusicListRowControl)sender).StopAutoScroll();

    private void StopAutoScroll()
    {
        if (AuthorScroll is not null) AuthorScroll.IsPlaying = false;
        if (AlbumScroll is not null) AlbumScroll.IsPlaying = false;
    }

    private void Row_Unloaded(object sender, RoutedEventArgs e) => StopAutoScroll();

    private void AuthorButton_Click(object sender, RoutedEventArgs e)
    {
        if (Music is { } music) ArtistInvoked?.Invoke(this, music);
    }

    private void AlbumButton_Click(object sender, RoutedEventArgs e)
    {
        if (Music is { } music) AlbumInvoked?.Invoke(this, music);
    }

    private void AutoScrollHover_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is AutoScrollView scroll) scroll.IsPlaying = true;
    }

    private void AutoScrollHover_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is AutoScrollView scroll) scroll.IsPlaying = false;
    }

    private void AutoScrollHover_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is AutoScrollView scroll) scroll.IsPlaying = false;
    }
}
