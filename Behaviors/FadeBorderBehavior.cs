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

            if (d is FadeBorderBehavior behavior && App.MainWindow.IsPlayingDetail)
            {
                var oldSource = e.OldValue as ImageSource;
                var newSource = e.NewValue as ImageSource;

                // 只要旧值存在就执行淡出动画（新值可以是 null）
                if (oldSource != null)
                {
                    behavior.TransitionWithImageSource(newSource, oldSource);
                }
                else
                {
                    // 如果旧值是 null，直接设置新值
                    behavior.SetImageSourceDirectly(newSource);
                }
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

            // 保存原始的 ImageBrush 引用
            if (AssociatedObject?.Background is ImageBrush imageBrush)
            {
                _originalImageBrush = imageBrush;
            }
        }

        private void TransitionWithImageSource(ImageSource? newSource, ImageSource? oldSource)
        {
            if (oldSource == null || AssociatedObject == null) return;

            // 如果控件不可见，直接更新，不执行动画
            if (AssociatedObject.Visibility == Visibility.Collapsed)
            {
                SetImageSourceDirectly(newSource);
                return;
            }

            // 确保有原始 ImageBrush
            if (_originalImageBrush == null && AssociatedObject.Background is ImageBrush brush)
            {
                _originalImageBrush = brush;
            }

            if (_originalImageBrush == null) return;

            // 清理之前的动画和临时对象
            CleanupTransition();

            // 复制 Transform（用于旧图）
            CompositeTransform transformClone = null;
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
                ImageSource = oldSource,
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
                Opacity = 1
            };

            // 直接更新原始 ImageBrush 的 ImageSource，保留 Transform
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

            _currentStoryboard.Completed += OnTransitionCompleted;

            _currentStoryboard.Begin();
        }

        private void OnTransitionCompleted(object sender, object e)
        {
            CleanupTransition();
        }

        private void CleanupTransition()
        {
            // 1. 停止并清理 Storyboard
            if (_currentStoryboard != null)
            {
                _currentStoryboard.Stop();
                _currentStoryboard.Completed -= OnTransitionCompleted; // 移除事件处理器
                _currentStoryboard.Children.Clear(); // 清理动画
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

        private void SetImageSourceDirectly(ImageSource? newSource)
        {
            if (AssociatedObject == null) return;

            // 确保有原始 ImageBrush
            if (_originalImageBrush == null && AssociatedObject.Background is ImageBrush brush)
            {
                _originalImageBrush = brush;
            }

            if (_originalImageBrush != null)
            {
                // 直接更新 ImageSource，保留 Transform
                _originalImageBrush.ImageSource = newSource;
            }
            else
            {
                // 如果没有原始 ImageBrush，创建一个新的
                var newBrush = new ImageBrush()
                {
                    ImageSource = newSource,
                    Stretch = Stretch.UniformToFill
                };
                AssociatedObject.Background = newBrush;
                _originalImageBrush = newBrush;
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