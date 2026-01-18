using DevWinUI;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using ZLinq;

namespace WinUIMusicPlayer.View
{
    public sealed partial class MusicBrowsePage
    {
        private SolidColorBrush _transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        private SolidColorBrush _whiteBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LyricsLineControl_IsCurrentLineEvent(object sender, RoutedEventArgs e)
        {
            var lyricsLineControl = (LyricsLineControl)sender;
            var container = ToolUtils.FindParent<ListViewItem>(lyricsLineControl);
            if (container == null) return;
            try
            {
                _scrollCancellation?.Cancel();
                _scrollCancellation = new CancellationTokenSource();
                var transform = container.TransformToVisual(LyricViewer.Content as UIElement);
                var targetPoint = transform.TransformPoint(new Point(0, 0));
                double startOffset = LyricViewer.VerticalOffset;
                double targetOffset = targetPoint.Y - (LyricViewer.ActualHeight / 2) + (container.ActualHeight / 2);
                LyricViewer.ScrollTo(0, targetOffset, _scrollOptions);
            }
            catch (OperationCanceledException) { }
            catch { }
        }       

        private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LyricLine lyricLine)
            {
                Task.Run(() =>
                {
                    int index = ViewModel.UILyrics.IndexOf(ViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.Time >= lyricLine.Time));
                    ViewModel.UpdateLyricsToUI(index);
                    ViewModel.isManualSelect = true;
                    ViewModel._musicPlaybackService.ChangeWaveChannelTime(lyricLine.Time);
                    ViewModel.isManualSelect = false;
                });
            }
        }

        private void LyricsLineGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid )
            {
                var blurControl = grid?.Children.AsValueEnumerable()
                          .OfType<BlurEffectControl>()
                          .FirstOrDefault();

                blurControl?.GetBlurEffectManager()?.StartBlurReverseAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(350));
                if (AppSettings.LyricsBlurAmount < 1)
                {
                    grid.Background = _whiteBrush;
                }             
            }
        }

        private void LyricsLineGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var blurControl = grid?.Children.AsValueEnumerable()
                        .OfType<BlurEffectControl>()
                        .FirstOrDefault();

                blurControl?.GetBlurEffectManager()?.StartBlurAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(350));

                if (AppSettings.LyricsBlurAmount<1)
                {
                    grid.Background = _transparentBrush;
                }

            }
        }
    }
}
