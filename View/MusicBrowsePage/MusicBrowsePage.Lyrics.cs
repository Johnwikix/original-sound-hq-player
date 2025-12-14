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
        private void ApplyVerticalGradientBlurToLyricViewer()
        {
            if (!AppSettings.IsBackgroundCoverEnabled) return;

            if (_isBlurApplied) return;

            var element = LyricViewer;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // 1. 创建容器视觉
            var containerVisual = compositor.CreateContainerVisual();
            containerVisual.Size = new System.Numerics.Vector2((float)element.ActualWidth, (float)element.ActualHeight);
            // 2. 创建清晰层
            var clearBrush = compositor.CreateBackdropBrush();
            var clearVisual = compositor.CreateSpriteVisual();
            clearVisual.Brush = clearBrush;
            clearVisual.Size = containerVisual.Size;
            // 3. 创建顶部模糊层
            var topBlurEffect = new GaussianBlurEffect
            {
                BlurAmount = 2f,
                Source = new CompositionEffectSourceParameter("Source"),
                Optimization = EffectOptimization.Speed,
                BorderMode = EffectBorderMode.Soft
            };

            var topEffectFactory = compositor.CreateEffectFactory(topBlurEffect);
            var topBackdropBrush = compositor.CreateBackdropBrush();
            var topEffectBrush = topEffectFactory.CreateBrush();
            topEffectBrush.SetSourceParameter("Source", topBackdropBrush);
            // 顶部模糊视觉
            var topBlurVisual = compositor.CreateSpriteVisual();
            topBlurVisual.Size = containerVisual.Size;
            topBlurVisual.Brush = topEffectBrush;
            // 顶部线性渐变遮罩（从上到中：不透明到透明）
            var topGradient = compositor.CreateLinearGradientBrush();
            topGradient.StartPoint = new System.Numerics.Vector2(0, 0);
            topGradient.EndPoint = new System.Numerics.Vector2(0, 0.5f);
            // 渐变停止点
            var topStop1 = compositor.CreateColorGradientStop(0.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255));
            var topStop2 = compositor.CreateColorGradientStop(1.0f, Windows.UI.Color.FromArgb(0, 255, 255, 255));
            topGradient.ColorStops.Add(topStop1);
            topGradient.ColorStops.Add(topStop2);
            // 顶部遮罩视觉
            var topMaskVisual = compositor.CreateSpriteVisual();
            topMaskVisual.Size = containerVisual.Size;
            topMaskVisual.Brush = topGradient;
            // 应用遮罩
            topBlurVisual.Brush = compositor.CreateMaskBrush();
            ((CompositionMaskBrush)topBlurVisual.Brush).Source = topEffectBrush;
            ((CompositionMaskBrush)topBlurVisual.Brush).Mask = topGradient;
            // 4. 创建底部模糊层
            var bottomBlurEffect = new GaussianBlurEffect
            {
                BlurAmount = 2f,
                Source = new CompositionEffectSourceParameter("Source"),
                Optimization = EffectOptimization.Speed,
                BorderMode = EffectBorderMode.Soft
            };

            var bottomEffectFactory = compositor.CreateEffectFactory(bottomBlurEffect);
            var bottomBackdropBrush = compositor.CreateBackdropBrush();
            var bottomEffectBrush = bottomEffectFactory.CreateBrush();
            bottomEffectBrush.SetSourceParameter("Source", bottomBackdropBrush);

            var bottomBlurVisual = compositor.CreateSpriteVisual();
            bottomBlurVisual.Size = containerVisual.Size;
            bottomBlurVisual.Brush = bottomEffectBrush;

            // 底部渐变遮罩（从中间到下：透明到不透明）
            var bottomGradient = compositor.CreateLinearGradientBrush();
            bottomGradient.StartPoint = new System.Numerics.Vector2(0, 0.5f);
            bottomGradient.EndPoint = new System.Numerics.Vector2(0, 1.0f);

            var bottomStop1 = compositor.CreateColorGradientStop(0.0f, Windows.UI.Color.FromArgb(0, 255, 255, 255));
            var bottomStop2 = compositor.CreateColorGradientStop(1.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255));
            bottomGradient.ColorStops.Add(bottomStop1);
            bottomGradient.ColorStops.Add(bottomStop2);

            bottomBlurVisual.Brush = compositor.CreateMaskBrush();
            ((CompositionMaskBrush)bottomBlurVisual.Brush).Source = bottomEffectBrush;
            ((CompositionMaskBrush)bottomBlurVisual.Brush).Mask = bottomGradient;

            // 组合所有层
            containerVisual.Children.InsertAtTop(clearVisual);
            containerVisual.Children.InsertAtTop(topBlurVisual);
            containerVisual.Children.InsertAtTop(bottomBlurVisual);

            ElementCompositionPreview.SetElementChildVisual(element, containerVisual);

            // 标记已应用模糊效果
            _isBlurApplied = true;
        }

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

        private Color GetResourceColor(string key, Color fallbackColor)
        {
            if (Application.Current.Resources.TryGetValue(key, out object resourceValue))
            {
                if (resourceValue is SolidColorBrush solidBrush)
                {
                    return solidBrush.Color;
                }
            }
            return fallbackColor;
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

        private void Grid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid )
            {
                var blurControl = grid?.Children.AsValueEnumerable()
                          .OfType<BlurEffectControl>()
                          .FirstOrDefault();

                blurControl?.GetBlurEffectManager()?.StartBlurReverseAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(1000));
            }
        }

        private void LyricsLineGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var blurControl = grid?.Children.AsValueEnumerable()
                        .OfType<BlurEffectControl>()
                        .FirstOrDefault();

                blurControl?.GetBlurEffectManager()?.StartBlurAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(1000));
            }
        }
    }
}
