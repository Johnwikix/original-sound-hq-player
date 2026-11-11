using WinUIMusicPlayer.Controls;
using System;
using System.Windows.Input;
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
    /// <summary>提供歌单出现项命令，普通行沿用 Music 命令。</summary>
    public static readonly DependencyProperty RowPlayCommandProperty = DependencyProperty.Register(
        nameof(RowPlayCommand), typeof(ICommand), typeof(MusicListRowControl), new PropertyMetadata(null));
    /// <summary>提供歌单出现项参数。</summary>
    public static readonly DependencyProperty RowPlayParameterProperty = DependencyProperty.Register(
        nameof(RowPlayParameter), typeof(object), typeof(MusicListRowControl), new PropertyMetadata(null));
    /// <summary>获取或设置出现项命令。</summary>
    public ICommand? RowPlayCommand { get => (ICommand?)GetValue(RowPlayCommandProperty); set => SetValue(RowPlayCommandProperty, value); }
    /// <summary>获取或设置出现项参数。</summary>
    public object? RowPlayParameter { get => GetValue(RowPlayParameterProperty); set => SetValue(RowPlayParameterProperty, value); }
    /// <summary>解析编译期可追踪的播放入口。</summary>
    public ICommand? PlayFor(ICommand? command, Music? music) => command ?? music?.PlayCommand;
    /// <summary>解析编译期可追踪的参数。</summary>
    public object? ParameterFor(object? parameter, Music? music) => parameter ?? music;

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

    public FrameworkElement CoverElement => CoverArtwork;
    public FrameworkElement TitleElement => MusicTitleText;

    public event EventHandler<Music>? SongInvoked;
    public event EventHandler<Music>? ArtistInvoked;
    public event EventHandler<Music>? AlbumInvoked;

    public MusicListRowControl() => InitializeComponent();

    private GridLength TrackColumnWidth(bool visible) => visible
        ? new GridLength(0.5, GridUnitType.Star) : new GridLength(0);

    private static void OnMusicChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((MusicListRowControl)sender).StopAutoScroll();

    public void StopAutoScroll()
    {
        if (TitleScroll is not null) TitleScroll.IsPlaying = false;
        if (AuthorScroll is not null) AuthorScroll.IsPlaying = false;
        if (AlbumScroll is not null) AlbumScroll.IsPlaying = false;
    }

    private void Row_Unloaded(object sender, RoutedEventArgs e) => StopAutoScroll();

    private void TitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (Music is { } music) SongInvoked?.Invoke(this, music);
    }

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
