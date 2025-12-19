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
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using ZLinq;

namespace WinUIMusicPlayer.View
{
    public sealed partial class MusicBrowsePage
    {       

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async void LyricsTextBlock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var textblock = (TextBlock)sender;
            bool isCurrentLyric = (!AppSettings.IsGlobalFontSizeEnabled &&
                                    (textblock.FontSize == 28 || textblock.FontSize == 32 ||
                                     textblock.FontSize == 36 || textblock.FontSize == 40 ||
                                     textblock.FontSize == 44)) ||
                                    (AppSettings.IsGlobalFontSizeEnabled &&
                                     textblock.FontSize == AppSettings.GlobalFontSize);
            var parentGrid = ToolUtils.FindParent<Grid>(textblock);
            if (isCurrentLyric)
            {
                StartTimerAnimation(textblock, ViewModel.LyricsDurationTime);               
                var container = ToolUtils.FindParent<ListViewItem>(textblock);
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
        }

        private void StartTimerAnimation(TextBlock textBlock, TimeSpan duration)
        {
            CancelCurrentAnimation();
            if(!AppSettings.IsWFWLyrics) return;
            var targetWidth = (float)textBlock.ActualWidth;
            if (targetWidth <= 0)
            {
                return;
            }
            var visual = ElementCompositionPreview.GetElementVisual(textBlock);
            var compositor = visual.Compositor;
            var clip = compositor.CreateInsetClip();
            clip.LeftInset = 0;
            clip.TopInset = 0;
            clip.BottomInset = 0;
            clip.RightInset = targetWidth;
            visual.Clip = clip;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = duration;
            animation.InsertKeyFrame(0.0f, targetWidth);
            animation.InsertKeyFrame(1.0f, 0.0f, compositor.CreateLinearEasingFunction());
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _currentAnimatingTextBlock = textBlock;
            _currentCompositionBatch = batch;
            _currentCompositionClip = clip;
            batch.Completed += OnCompositionBatchCompleted;
            clip.StartAnimation("RightInset", animation);            
            batch.End();
        }

        private void OnCompositionBatchCompleted(object sender, CompositionBatchCompletedEventArgs args)
        {
            var batch = (CompositionScopedBatch)sender;
            batch.Completed -= OnCompositionBatchCompleted;
            _currentCompositionBatch = null;
            _currentCompositionClip = null;
            _currentAnimatingTextBlock?.Clip = null;
            _currentAnimatingTextBlock = null;
        }

        // 取消当前动画
        public void CancelCurrentAnimation()
        {
            _currentCompositionBatch?.Completed -= OnCompositionBatchCompleted;
            _currentCompositionBatch = null;
            _currentCompositionClip?.StopAnimation("RightInset");
            _currentCompositionClip = null;
            _currentAnimatingTextBlock?.Clip = null;
            _currentAnimatingTextBlock = null;
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
                if (AppSettings.IsLyricsLineBlurEnabled)
                {
                    var blurControl = grid?.Children.AsValueEnumerable()
                          .OfType<BlurEffectControl>()
                          .FirstOrDefault();

                    blurControl?.GetBlurEffectManager()?.StartBlurReverseAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(1000));
                }
                else {
                    grid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 128, 128, 128));
                }                
            }
        }

        private void LyricsLineGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                if (AppSettings.IsLyricsLineBlurEnabled)
                {
                    var blurControl = grid?.Children.AsValueEnumerable()
                        .OfType<BlurEffectControl>()
                        .FirstOrDefault();

                    blurControl?.GetBlurEffectManager()?.StartBlurAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(1000));
                }
                else
                {
                    grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }

            }
        }
    }
}
