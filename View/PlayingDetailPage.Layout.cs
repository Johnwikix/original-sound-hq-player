using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

public sealed partial class PlayingDetailPage
{
    private Windows.Foundation.Size _playbackViewport;
    private bool _isPortraitLayout;
    private const double PortraitEnterRatio = 1.15;
    private const double PortraitExitRatio = 1.10;
    private const double PortraitTopVerticalMargin = 20;
    private const double PortraitTextScale = 1.6;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        // Choose the cover's slot and the text metrics before measuring either renderer.
        if (PlayingDetail is not null && availableSize != _playbackViewport
            && double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height)
            && availableSize.Width > 0 && availableSize.Height > 0)
        {
            _playbackViewport = availableSize;
            ApplyPlaybackLayout(availableSize);
        }
        return base.MeasureOverride(availableSize);
    }

    private void ApplyPlaybackLayout(Windows.Foundation.Size viewport)
    {
        bool portrait = viewport.Height / viewport.Width > (_isPortraitLayout ? PortraitExitRatio : PortraitEnterRatio);
        var (lyrics, title, artist, info, topHeight) = viewport.Width switch
        {
            <= 1024 => (32, 24, 20, 10, 260),
            < 1280 => (36, 26, 22, 11, 280),
            < 1600 => (42, 32, 24, 13, 300),
            < 1920 => (48, 36, 30, 14, 320),
            < 2560 => (56, 42, 34, 15, 340),
            < 2880 => (64, 48, 38, 16, 360),
            _ => (76, 56, 44, 18, 380)
        };

        if (portrait)
        {
            lyrics = (int)(lyrics * AppViewModel.PortraitLyricsScale);
            title = (int)(title * PortraitTextScale);
            artist = (int)(artist * PortraitTextScale);
            info = (int)(info * PortraitTextScale);
        }

        ViewModel.TitleFontSize = title;
        ViewModel.ArtistAlbumFontSize = artist;
        ViewModel.InfoFontSize = info;
        ViewModel.AppViewModel.LyricsFontSize = lyrics;
        ApplyAspectRatioLayout(portrait, topHeight);
    }

    private void ApplyAspectRatioLayout(bool portrait, double topHeight)
    {
        if (portrait)
            LeftControlPanel.Height = topHeight;
        ViewModel.AppViewModel.IsPortraitLayout = portrait;
        if (_isPortraitLayout == portrait) return;
        _isPortraitLayout = portrait;

        if (portrait)
        {
            // Z-order: LyricsRegionHost 最底层，LeftControlPanel 浮在上面（不用 Remove/Insert 避免 Unloaded 触发 PrepareForShutdown）
            Canvas.SetZIndex(LyricsRegionHost, 0);
            Canvas.SetZIndex(LeftControlPanel, 2);

            PlayingDetail.RowDefinitions.Clear();
            PlayingDetail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            PlayingDetail.ColumnDefinitions.Clear();
            PlayingDetail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // LyricsRegionHost 占满全页
            Grid.SetRow(LyricsRegionHost, 0);
            Grid.SetColumn(LyricsRegionHost, 0);
            Grid.SetRowSpan(LyricsRegionHost, 1);
            Grid.SetColumnSpan(LyricsRegionHost, 1);
            LyricsRegionHost.Margin = new Thickness(0, 60, 0, 0);

            // LeftControlPanel 固定在顶部，浮在 lyrics 之上
            LeftControlPanel.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(LeftControlPanel, 0);
            Grid.SetColumn(LeftControlPanel, 0);
            Grid.SetRowSpan(LeftControlPanel, 1);
            Grid.SetColumnSpan(LeftControlPanel, 1);

            LeftControlPanel.ColumnDefinitions.Clear();
            LeftControlPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            LeftControlPanel.Margin = new Thickness(10, PortraitTopVerticalMargin, 10, 0);
            LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(topHeight, GridUnitType.Pixel) });
            LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            LeftControlPanel.RowDefinitions.Clear();
            LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(CoverContainer, 0);
            Grid.SetRowSpan(CoverContainer, 3);
            Grid.SetColumn(CoverContainer, 0);
            Grid.SetColumnSpan(CoverContainer, 1);

            AnimatedTextBlock.Margin = new Thickness(16, 0, 16, 0);
            Grid.SetRow(AnimatedTextBlock, 1);
            Grid.SetColumn(AnimatedTextBlock, 1);
            Grid.SetRowSpan(AnimatedTextBlock, 1);
            Grid.SetColumnSpan(AnimatedTextBlock, 1);
        }
        else
        {
            PlayingDetail.RowDefinitions.Clear();
            PlayingDetail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            PlayingDetail.ColumnDefinitions.Clear();
            PlayingDetail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PlayingDetail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 还原 Z-order
            LyricsRegionHost.ClearValue(Canvas.ZIndexProperty);
            LeftControlPanel.ClearValue(Canvas.ZIndexProperty);

            LeftControlPanel.ClearValue(FrameworkElement.HeightProperty);
            LeftControlPanel.ClearValue(FrameworkElement.VerticalAlignmentProperty);
            LeftControlPanel.ClearValue(FrameworkElement.MarginProperty);
            LeftControlPanel.Background = null;

            Grid.SetRow(LeftControlPanel, 0);
            Grid.SetColumn(LeftControlPanel, 0);
            Grid.SetRowSpan(LeftControlPanel, 1);
            Grid.SetColumnSpan(LeftControlPanel, 1);

            LeftControlPanel.ColumnDefinitions.Clear();
            LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
            LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            LeftControlPanel.RowDefinitions.Clear();
            LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6, GridUnitType.Star) });
            LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50, GridUnitType.Pixel) });
            LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            CoverContainer.ClearValue(FrameworkElement.MarginProperty);
            Grid.SetRow(CoverContainer, 1);
            Grid.SetRowSpan(CoverContainer, 1);
            Grid.SetColumn(CoverContainer, 1);
            Grid.SetColumnSpan(CoverContainer, 1);
            AnimatedTextBlock.ClearValue(FrameworkElement.VerticalAlignmentProperty);
            AnimatedTextBlock.ClearValue(FrameworkElement.MarginProperty);
            AnimatedTextBlock.Margin = new Thickness(20, 0, 20, 0);
            Grid.SetRow(AnimatedTextBlock, 2);
            Grid.SetColumn(AnimatedTextBlock, 1);
            Grid.SetRowSpan(AnimatedTextBlock, 1);
            Grid.SetColumnSpan(AnimatedTextBlock, 1);

            Grid.SetRow(LyricsRegionHost, 0);
            Grid.SetColumn(LyricsRegionHost, 1);
            Grid.SetRowSpan(LyricsRegionHost, 1);
            Grid.SetColumnSpan(LyricsRegionHost, 1);
            LyricsRegionHost.Margin = new Thickness(0, 40, 0, 40);
        }

        ScheduleLyricsRegionUpdate();
    }
}
