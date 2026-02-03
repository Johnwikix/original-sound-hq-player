using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Xaml.Interactivity;
using System;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Behaviors
{
    public class FadeBorderBehavior : Behavior<Border>
    {
        private Storyboard? _currentStoryboard;
        private Border? _tempCoverBorder;
        private ImageBrush? _originalImageBrush;

        public ImageSource Source
        {
            get { return (ImageSource)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(ImageSource), typeof(FadeBorderBehavior),
                new PropertyMetadata(null, OnSourceChanged));

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!AppSettings.IsBackgroundCoverEnabled)
            {
                return;
            }

            if (d is FadeBorderBehavior behavior)
            {
                var newSource = e.NewValue as ImageSource;
                behavior.TransitionToNewSource(newSource);
            }
        }

        public Duration Duration
        {
            get { return (Duration)GetValue(DurationProperty); }
            set { SetValue(DurationProperty, value); }
        }

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register("Duration", typeof(Duration), typeof(FadeBorderBehavior),
                new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(300))));

        protected override void OnAttached()
        {
            base.OnAttached();

            // 当重新挂载时，确保 Border 显示的是当前 Behavior 记录的最新的 Source
            if (AssociatedObject != null)
            {
                // 保存或创建原始的 ImageBrush 引用
                if (AssociatedObject.Background is ImageBrush imageBrush)
                {
                    _originalImageBrush = imageBrush;
                }
                else if (AssociatedObject.Background == null)
                {
                    // 如果没有背景，创建一个新的 ImageBrush
                    _originalImageBrush = new ImageBrush()
                    {
                        Stretch = Stretch.UniformToFill
                    };
                    AssociatedObject.Background = _originalImageBrush;
                }

                // 确保显示最新的 Source
                if (_originalImageBrush != null)
                {
                    _originalImageBrush.ImageSource = Source;
                }
            }
        }

        private void TransitionToNewSource(ImageSource? newSource)
        {
            if (AssociatedObject == null) return;

            // 确保有原始 ImageBrush
            if (_originalImageBrush == null)
            {
                if (AssociatedObject.Background is ImageBrush brush)
                {
                    _originalImageBrush = brush;
                }
                else
                {
                    // 如果没有背景，创建一个新的 ImageBrush
                    _originalImageBrush = new ImageBrush()
                    {
                        Stretch = Stretch.UniformToFill
                    };
                    AssociatedObject.Background = _originalImageBrush;
                }
            }

            // 获取当前显示的图片（从 ImageBrush 中）
            var currentSource = _originalImageBrush.ImageSource;

            // 如果新旧完全一致（包括同为 null），则不执行动画
            if (currentSource == newSource) return;

            // 如果控件不可见，直接更新，不执行动画
            if (AssociatedObject.Visibility == Visibility.Collapsed)
            {
                _originalImageBrush.ImageSource = newSource;
                return;
            }

            // 清理之前的动画和临时对象
            CleanupTransition();

            // 只有当"旧图"存在时，才需要创建临时层来执行淡出
            // 如果旧图本来就是空的，直接设置新图即可
            if (currentSource != null)
            {
                // 复制 Transform（用于旧图）
                CompositeTransform? transformClone = null;
                if (_originalImageBrush.RelativeTransform is CompositeTransform composite)
                {
                    transformClone = new CompositeTransform
                    {
                        CenterX = composite.CenterX,
                        CenterY = composite.CenterY,
                        Rotation = composite.Rotation,
                        ScaleX = composite.ScaleX,
                        ScaleY = composite.ScaleY,
                        SkewX = composite.SkewX,
                        SkewY = composite.SkewY,
                        TranslateX = composite.TranslateX,
                        TranslateY = composite.TranslateY
                    };
                }

                // 创建旧图的 ImageBrush
                var oldBrush = new ImageBrush()
                {
                    ImageSource = currentSource,
                    Stretch = _originalImageBrush.Stretch,
                    RelativeTransform = transformClone
                };

                // 创建临时 Border 显示旧图
                _tempCoverBorder = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = oldBrush,
                    CornerRadius = AssociatedObject.CornerRadius,
                    Opacity = 1,
                    IsHitTestVisible = false
                };

                // 在旧图开始淡出的同时，把底层原始 ImageBrush 设为新值（可能是 null）
                _originalImageBrush.ImageSource = newSource;

                // 叠加临时 Border
                AssociatedObject.Child = _tempCoverBorder;

                // 创建淡出动画
                var ani = new DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    Duration = Duration.TimeSpan,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                _currentStoryboard = new Storyboard();
                _currentStoryboard.Children.Add(ani);

                Storyboard.SetTarget(ani, _tempCoverBorder);
                Storyboard.SetTargetProperty(ani, "Opacity");

                _currentStoryboard.Completed += (s, e) =>
                {
                    CleanupTransition();
                };

                _currentStoryboard.Begin();
            }
            else
            {
                // 如果旧图是空的，直接更新 ImageSource（可能设置为新图或保持为 null）
                _originalImageBrush.ImageSource = newSource;
            }
        }

        private void CleanupTransition()
        {
            // 1. 停止并清理 Storyboard
            if (_currentStoryboard != null)
            {
                _currentStoryboard.Stop();
                // 移除 Completed 事件处理器防止内存泄漏
                // 注意：如果是匿名函数，销毁 Storyboard 对象本身即可
                _currentStoryboard = null;
            }

            // 2. 清理临时 Border
            if (_tempCoverBorder != null)
            {
                // 从父容器移除
                if (AssociatedObject?.Child == _tempCoverBorder)
                {
                    AssociatedObject.Child = null;
                }

                // 释放 ImageBrush 及其 ImageSource 引用
                if (_tempCoverBorder.Background is ImageBrush brush)
                {
                    brush.ImageSource = null; // 断开对旧图的引用，允许 GC 回收
                    brush.RelativeTransform = null;
                }

                _tempCoverBorder.Background = null;
                _tempCoverBorder = null;
            }
        }

        protected override void OnDetaching()
        {
            // 彻底清理所有资源
            CleanupTransition();

            // 清理原始 ImageBrush 引用
            _originalImageBrush = null;

            base.OnDetaching();
        }
    }
}