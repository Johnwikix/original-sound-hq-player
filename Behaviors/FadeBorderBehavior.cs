using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.Text;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Behaviors
{
    public class FadeBorderBehavior : Behavior<Border>
    {
        private Storyboard _currentImageTransitionStoryboard;

        //public Brush Brush
        //{
        //    get { return (Brush)GetValue(BrushProperty); }
        //    set { SetValue(BrushProperty, value); }
        //}

        //public static readonly DependencyProperty BrushProperty =
        //    DependencyProperty.Register("Brush", typeof(Brush), typeof(FadeBorderBehavior),
        //        new PropertyMetadata(null, OnBrushChanged));

        //private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        //{
        //    //if (d is FadeBorderBehavior behavior && e.NewValue is Brush brush && App.MainWindow.IsPlayingDetail)
        //    //{
        //    //    behavior.TransitionToNewBrush(brush);
        //    //}
        //}

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
                var oldSource = e.OldValue as ImageSource;
                var newSource = e.NewValue as ImageSource;

                // 只要旧值存在就执行淡出动画（新值可以是 null）
                if (oldSource != null)
                {
                    behavior.TransitionWithImageSource(newSource, oldSource);
                }
                else
                {
                    // 如果旧值是 null，直接设置新值（可能也是 null）
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

        //private void TransitionToNewBrush(Brush newBrush)
        //{
        //    if (newBrush == null || AssociatedObject == null) return;
        //    // 如果控件不可见，直接更新，不执行动画
        //    //if (AssociatedObject.Visibility == Visibility.Collapsed)
        //    //{
        //    //    AssociatedObject.Background = newBrush;
        //    //    return;
        //    //}

        //    var oldBrush = AssociatedObject.Background;
        //    if (oldBrush == null)
        //    {
        //        AssociatedObject.Background = newBrush;
        //        return;
        //    }

        //    if (_currentImageTransitionStoryboard != null)
        //    {
        //        _currentImageTransitionStoryboard.Stop();
        //        AssociatedObject.Child = null;
        //        _currentImageTransitionStoryboard = null;
        //    }

        //    var cover = new Border
        //    {
        //        HorizontalAlignment = HorizontalAlignment.Stretch,
        //        VerticalAlignment = VerticalAlignment.Stretch,
        //        Background = newBrush,
        //        CornerRadius = AssociatedObject.CornerRadius,
        //        Opacity = 0
        //    };

        //    AssociatedObject.Child = cover;

        //    var ani = new DoubleAnimation
        //    {
        //        From = 0,
        //        To = 1,
        //        Duration = Duration.TimeSpan,
        //        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        //    };

        //    var storyboard = new Storyboard();
        //    storyboard.Children.Add(ani);

        //    Storyboard.SetTarget(ani, cover);
        //    Storyboard.SetTargetProperty(ani, "Opacity");
        //    _currentImageTransitionStoryboard = storyboard;

        //    storyboard.Completed += (s, e) =>
        //    {
        //        AssociatedObject.Child = null;
        //        AssociatedObject.Background = newBrush;
        //        if (storyboard == _currentImageTransitionStoryboard)
        //        {
        //            _currentImageTransitionStoryboard = null;
        //        }
        //    };

        //    storyboard.Begin();
        //}

        private void TransitionWithImageSource(ImageSource newSource, ImageSource oldSource)
        {
            if (oldSource == null || AssociatedObject == null) return;
            // 如果控件不可见，直接更新，不执行动画
            if (AssociatedObject.Visibility == Visibility.Collapsed)
            {
                SetImageSourceDirectly(newSource);
                return;
            }

            // 获取当前 Background 的 ImageBrush 配置
            ImageBrush currentImageBrush = AssociatedObject.Background as ImageBrush;
            if (currentImageBrush == null) return;

            if (_currentImageTransitionStoryboard != null)
            {
                _currentImageTransitionStoryboard.Stop();
                AssociatedObject.Child = null;
                _currentImageTransitionStoryboard = null;
            }

            // 复制 Transform（用于旧图）
            CompositeTransform transformClone = null;
            if (currentImageBrush.RelativeTransform is CompositeTransform composite)
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
                Stretch = currentImageBrush.Stretch,
                RelativeTransform = transformClone
            };

            // 创建 Child Border 显示旧图（从 1 淡出到 0）
            var cover = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = oldBrush,
                CornerRadius = AssociatedObject.CornerRadius,
                Opacity = 1
            };

            // 先立即设置新的 Background（可能是 null）
            if (newSource != null)
            {
                var newBrush = new ImageBrush()
                {
                    ImageSource = newSource,
                    Stretch = currentImageBrush.Stretch,
                    RelativeTransform = currentImageBrush.RelativeTransform
                };
                AssociatedObject.Background = newBrush;
            }
            else
            {
                // 新值是 null，清空 Background
                AssociatedObject.Background = null;
            }

            // Child 叠加在上方显示旧图
            AssociatedObject.Child = cover;

            // 淡出动画：从 1 到 0
            var ani = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = Duration.TimeSpan,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var storyboard = new Storyboard();
            storyboard.Children.Add(ani);

            Storyboard.SetTarget(ani, cover);
            Storyboard.SetTargetProperty(ani, "Opacity");

            _currentImageTransitionStoryboard = storyboard;

            storyboard.Completed += (s, e) =>
            {
                // 动画完成后，移除显示旧图的 Child
                AssociatedObject.Child = null;

                if (storyboard == _currentImageTransitionStoryboard)
                {
                    _currentImageTransitionStoryboard = null;
                }
            };

            storyboard.Begin();
        }

        private void SetImageSourceDirectly(ImageSource newSource)
        {
            if (AssociatedObject == null) return;

            if (newSource != null)
            {
                ImageBrush currentImageBrush = AssociatedObject.Background as ImageBrush;
                var newBrush = new ImageBrush()
                {
                    ImageSource = newSource,
                    Stretch = currentImageBrush?.Stretch ?? Stretch.UniformToFill,
                    RelativeTransform = currentImageBrush?.RelativeTransform
                };
                AssociatedObject.Background = newBrush;
            }
            else
            {
                AssociatedObject.Background = null;
            }
        }

        protected override void OnDetaching()
        {
            if (_currentImageTransitionStoryboard != null)
            {
                _currentImageTransitionStoryboard.Stop();
                _currentImageTransitionStoryboard = null;
            }

            if (AssociatedObject != null)
            {
                AssociatedObject.Child = null;
            }

            base.OnDetaching();
        }
    }
}