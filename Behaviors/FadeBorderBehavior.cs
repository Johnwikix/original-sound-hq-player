using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.Text;

namespace WinUIMusicPlayer.Behaviors
{
    public class FadeBorderBehavior : Behavior<Border>
    {
        public Brush Brush
        {
            get { return (Brush)GetValue(BrushProperty); }
            set { SetValue(BrushProperty, value); }
        }

        public static readonly DependencyProperty BrushProperty =
            DependencyProperty.Register("Brush", typeof(Brush), typeof(FadeBorderBehavior),
                new PropertyMetadata(null, OnBrushChanged));

        private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FadeBorderBehavior behavior && e.NewValue is Brush brush && App.MainWindow.IsPlayingDetail)
            {
                behavior.TransitionToNewBrush(brush);
            }
        }



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
            if (d is FadeBorderBehavior behavior && e.OldValue is ImageSource source && App.MainWindow.IsPlayingDetail)
            {
                behavior.TransitionWithImageSource(source);
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

        private void TransitionToNewBrush(Brush newBrush)
        {
            if (newBrush == null || AssociatedObject == null) return;

            var oldBrush = AssociatedObject.Background;
            if (oldBrush == null)
            {
                AssociatedObject.Background = newBrush;
                return;
            }

            var cover = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = newBrush,
                CornerRadius = AssociatedObject.CornerRadius,
                Opacity = 0
            };

            AssociatedObject.Child = cover;

            var ani = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = Duration.TimeSpan,
                EasingFunction = new CubicEase()
            };

            // 1. 创建 Storyboard
            var storyboard = new Storyboard();
            storyboard.Children.Add(ani);

            // 2. 设置动画目标和属性
            Storyboard.SetTarget(ani, cover);
            Storyboard.SetTargetProperty(ani, "Opacity");

            // 3. 注册完成事件
            storyboard.Completed += (s, e) =>
            {
                AssociatedObject.Child = null;
                AssociatedObject.Background = newBrush;
            };

            // 4. 启动动画
            storyboard.Begin();
        }

        private void TransitionWithImageSource(ImageSource oldSource)
        {
            if (oldSource == null || AssociatedObject == null) return;
            if (AssociatedObject.Background is not ImageBrush image) return;
            CompositeTransform transformClone = null;
            if (image.RelativeTransform is CompositeTransform composite)
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

            var oldBrush = new ImageBrush()
            {
                ImageSource = oldSource,
                Stretch = image.Stretch,
                RelativeTransform = transformClone // 使用手动复制的 Transform
            };

            var cover = new Border
            {
                Background = oldBrush,
                CornerRadius = AssociatedObject.CornerRadius
            };
            AssociatedObject.Child = cover;

            var ani = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = Duration.TimeSpan, // 修正 Duration
                EasingFunction = new CubicEase()
            };

            // 替换 BeginAnimation 为 Storyboard 逻辑
            var storyboard = new Storyboard();
            storyboard.Children.Add(ani);

            Storyboard.SetTarget(ani, cover);
            Storyboard.SetTargetProperty(ani, "Opacity");

            storyboard.Completed += (s, e) =>
            {
                AssociatedObject.Child = null;
            };

            storyboard.Begin(); // 启动 Storyboard
        }
    }

}
