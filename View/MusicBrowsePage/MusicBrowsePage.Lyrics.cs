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
using System.Linq;
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

        private void ApplyBlurToArea(FrameworkElement element, float blurAmount)
        {
            if (element is null) return;
            if (!AppSettings.IsBackgroundCoverEnabled || AppSettings.IsGradientBlurEnabled)
            {
                ElementCompositionPreview.SetElementChildVisual(element, null);
                return;
            }
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            // 创建高斯模糊效果
            var gaussianBlurEffect = new GaussianBlurEffect
            {
                BlurAmount = blurAmount,
                Source = new CompositionEffectSourceParameter("Source"),
                Optimization = EffectOptimization.Balanced,
                BorderMode = EffectBorderMode.Soft
            };
            // 创建效果工厂
            var effectFactory = compositor.CreateEffectFactory(gaussianBlurEffect);
            var backdropBrush = compositor.CreateBackdropBrush();
            var effectBrush = effectFactory.CreateBrush();
            effectBrush.SetSourceParameter("Source", backdropBrush);
            var spriteVisual = compositor.CreateSpriteVisual();
            spriteVisual.Size = new System.Numerics.Vector2((float)element.ActualWidth, (float)element.ActualHeight);
            spriteVisual.Brush = effectBrush;
            ElementCompositionPreview.SetElementChildVisual(element, spriteVisual);
        }

        private void ClearBlurFromLyricViewer()
        {
            if (!_isBlurApplied) return;

            ElementCompositionPreview.SetElementChildVisual(LyricViewer, null);
            _isBlurApplied = false;
        }

        // 在LyricViewer的SizeChanged事件中调用
        private void LyricViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (AppSettings.IsGradientBlurEnabled)
            {
                if (_isBlurApplied)
                {
                    ClearBlurFromLyricViewer();
                }
                ApplyVerticalGradientBlurToLyricViewer();
            }
            else {
                ClearBlurFromLyricViewer();
            }
        }

        private void LyricViewer_ViewChanged(ScrollView sender, object args)
        {
            UpdateLyricsOpacity();
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
                ApplyBlurToArea(parentGrid, 0);
                //StartTimerAnimation(textblock, ViewModel.LyricsDurationTime);
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
                    //if (AppSettings.IsAnimateScrollEnabled)
                    //{
                    //    await AnimateScrollAsync(startOffset, targetOffset, _scrollCancellation.Token);
                    //}
                    //else
                    //{
                    //    LyricViewer.ScrollTo(0, targetOffset, _scrollOptions);    
                    //}
                    LyricViewer.ScrollTo(0, targetOffset, _scrollOptions);
                }
                catch (OperationCanceledException) { }
                catch { }
            }
            else
            {
                ApplyBlurToArea(parentGrid, 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task AnimateScrollAsync(double startOffset, double targetOffset, CancellationToken cancellationToken, int duration = 400, int fps = 100)
        {
            double distance = targetOffset - startOffset;
            if (Math.Abs(distance) < 1)
            {
                //LyricViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
                LyricViewer.ScrollTo(0, targetOffset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
                return;
            }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long targetFrameTimeMs = 1000 / fps;
            long nextFrameTimeMs = 0;
            while (stopwatch.ElapsedMilliseconds < duration)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                double progress = (double)stopwatch.ElapsedMilliseconds / duration;
                progress = Math.Min(progress, 1.0);
                double easedProgress = 1 - Math.Pow(1 - progress, 3);
                double currentOffset = startOffset + distance * easedProgress;
                //LyricViewer.ChangeView(null, currentOffset, null, disableAnimation: true);
                LyricViewer.ScrollTo(0, currentOffset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
                nextFrameTimeMs += targetFrameTimeMs;
                long timeToWait = nextFrameTimeMs - stopwatch.ElapsedMilliseconds;
                if (timeToWait > 0)
                {
                    await Task.Delay((int)timeToWait, cancellationToken);
                }
            }
        }

        private void UpdateLyricsOpacity(double maxOpacity = 0.6, double minOpacity = 0.01)
        {
            double viewerHeight = LyricViewer.ActualHeight;
            double viewerCenter = LyricViewer.VerticalOffset + (viewerHeight / 2);
            double maxDistance = viewerHeight / 2.2;
            double opacityRange = maxOpacity - minOpacity;
            var panel = LyricsListView.ItemsPanelRoot as ItemsStackPanel;
            if (panel == null) return;
            for (int i = panel.FirstVisibleIndex; i <= panel.LastVisibleIndex; i++)
            {
                var itemContainer = LyricsListView.ContainerFromIndex(i) as ListViewItem;
                if (itemContainer == null) continue;

                var lyricGrid = itemContainer.ContentTemplateRoot as Grid;
                if (lyricGrid == null) continue;

                var baseTextBlock = lyricGrid.FindName("LyricsTextBlockBase") as TextBlock;
                if (baseTextBlock == null) continue;

                var transform = itemContainer.TransformToVisual(LyricViewer.Content as UIElement);
                var itemTop = transform.TransformPoint(new Point(0, 0)).Y;

                double itemCenter = itemTop + (itemContainer.ActualHeight / 2);

                double distance = Math.Abs(itemCenter - viewerCenter);

                double opacity;

                if (distance >= maxDistance)
                {
                    opacity = minOpacity;
                }
                else
                {
                    double normalizedDistance = distance / maxDistance;
                    opacity = maxOpacity - (normalizedDistance * opacityRange);
                }
                baseTextBlock.Opacity = opacity;
            }
        }

        private void StartTimerAnimation(TextBlock textBlock, TimeSpan duration)
        {
            CancelCurrentAnimation();
            // 初始化 Clip
            var clipGeometry = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, 0, textBlock.ActualHeight)
            };
            textBlock.Clip = clipGeometry;

            var startTime = DateTime.Now;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps

            timer.Tick += (s, e) =>
            {
                var elapsed = DateTime.Now - startTime;
                var progress = Math.Min(1.0, elapsed.TotalSeconds / duration.TotalSeconds);
                var width = textBlock.ActualWidth * progress;

                clipGeometry.Rect = new Windows.Foundation.Rect(0, 0, width, textBlock.ActualHeight);

                if (progress >= 1.0)
                {
                    timer.Stop();
                    textBlock.Clip = null;
                    _currentAnimationTimer = null;
                    _currentAnimatingTextBlock = null;
                }
            };
            // 缓存当前动画
            _currentAnimatingTextBlock = textBlock;
            _currentAnimationTimer = timer;
            timer.Start();
        }

        // 取消当前动画
        public void CancelCurrentAnimation()
        {
            if (_currentAnimationTimer != null)
            {
                _currentAnimationTimer.Stop();
                _currentAnimationTimer = null;
            }

            if (_currentAnimatingTextBlock != null)
            {
                _currentAnimatingTextBlock.Clip = null; // 清除裁剪,恢复完整显示
                _currentAnimatingTextBlock = null;
            }
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

        private void LyricsTextBlock_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                if (AppSettings.IsGradientBlurEnabled || !AppSettings.IsBackgroundCoverEnabled)
                {
                    textBlock.Opacity = textBlock.Opacity + 0.2;
                }
                else
                {
                    var parentGrid = ToolUtils.FindParent<Grid>(textBlock);
                    ApplyBlurToArea(parentGrid, 0);

                }
            }
        }

        private void LyricsTextBlock_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.DataContext is LyricLine lyricLine)
            {
                var parentGrid = ToolUtils.FindParent<Grid>(textBlock);
                if (!lyricLine.IsCurrent)
                {
                    if (AppSettings.IsGradientBlurEnabled || !AppSettings.IsBackgroundCoverEnabled)
                    {
                        textBlock.Opacity = 0;
                    }
                    else
                    {
                        ApplyBlurToArea(parentGrid, 1);
                    }
                }
                else
                {
                    if (AppSettings.IsGradientBlurEnabled || !AppSettings.IsBackgroundCoverEnabled)
                    {
                        textBlock.Opacity = textBlock.Opacity + 0.2;
                    }
                    else
                    {
                        ApplyBlurToArea(parentGrid, 0);
                    }
                }
            }
        }
    }
}
