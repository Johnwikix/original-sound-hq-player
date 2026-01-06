using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Xaml.Interactivity;
using System;

namespace WinUIMusicPlayer.Behaviors
{
    /// <summary>
    /// 针对 Image 控件的淡入淡出 Behavior
    /// </summary>
    public class FadeImageBehavior : Behavior<Image>
    {
        private Storyboard _currentTransitionStoryboard;
        private Image _tempOverlayImage;

        public ImageSource Source
        {
            get { return (ImageSource)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(ImageSource), typeof(FadeImageBehavior),
                new PropertyMetadata(null, OnSourceChanged));

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FadeImageBehavior behavior)
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
            DependencyProperty.Register("Duration", typeof(Duration), typeof(FadeImageBehavior),
                new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(500))));

        private void TransitionToNewSource(ImageSource newSource)
        {
            if (AssociatedObject == null) return;

            // 如果新旧完全一致（包括同为 null），则不执行动画
            if (AssociatedObject.Source == newSource) return;

            var parent = VisualTreeHelper.GetParent(AssociatedObject) as Panel;
            if (parent == null)
            {
                AssociatedObject.Source = newSource;
                return;
            }

            StopAndCleanup();

            // 只有当“旧图”存在时，才需要创建临时层来执行淡出
            // 如果旧图本来就是空的，直接设置新图即可
            if (AssociatedObject.Source != null)
            {
                _tempOverlayImage = new Image
                {
                    Source = AssociatedObject.Source,
                    Stretch = AssociatedObject.Stretch,
                    HorizontalAlignment = AssociatedObject.HorizontalAlignment,
                    VerticalAlignment = AssociatedObject.VerticalAlignment,
                    Opacity = 1,
                    IsHitTestVisible = false
                };

                int currentZIndex = Canvas.GetZIndex(AssociatedObject);
                Canvas.SetZIndex(_tempOverlayImage, currentZIndex + 1);
                parent.Children.Add(_tempOverlayImage);

                // 创建淡出动画
                var ani = new DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    Duration = Duration.TimeSpan,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                _currentTransitionStoryboard = new Storyboard();
                _currentTransitionStoryboard.Children.Add(ani);
                Storyboard.SetTarget(ani, _tempOverlayImage);
                Storyboard.SetTargetProperty(ani, "Opacity");

                _currentTransitionStoryboard.Completed += (s, e) =>
                {
                    StopAndCleanup();
                };

                // 在旧图开始淡出的同时，把底层原图设为新值 (可能是 null)
                AssociatedObject.Source = newSource;
                _currentTransitionStoryboard.Begin();
            }
            else
            {
                // 如果旧图是空的，直接更新 Source，无需动画（或可选做一个简单的淡入）
                AssociatedObject.Source = newSource;
            }
        }

        private void StopAndCleanup()
        {
            // 1. 停止并清理 Storyboard
            if (_currentTransitionStoryboard != null)
            {
                _currentTransitionStoryboard.Stop();
                // 移除 Completed 事件处理器防止内存泄漏
                // 注意：如果是匿名函数，销毁 Storyboard 对象本身即可
                _currentTransitionStoryboard = null;
            }

            // 2. 彻底释放临时 Image 资源
            if (_tempOverlayImage != null)
            {
                var parent = VisualTreeHelper.GetParent(_tempOverlayImage) as Panel;
                if (parent != null)
                {
                    parent.Children.Remove(_tempOverlayImage);
                }

                // 重要：将 Source 置为空，断开对 ImageSource (BitmapImage) 的引用
                // 这允许 GC 回收旧图片的内存
                _tempOverlayImage.Source = null;
                _tempOverlayImage = null;
            }
        }

        private void RemoveTempImage(Panel parent)
        {
            if (_tempOverlayImage != null && parent != null)
            {
                parent.Children.Remove(_tempOverlayImage);
                _tempOverlayImage = null;
            }
        }

        protected override void OnDetaching()
        {
            StopAndCleanup();
            base.OnDetaching();
        }
    }
}