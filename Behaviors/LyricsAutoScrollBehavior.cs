using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.Text;
using Windows.Foundation;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Behaviors
{
    public class LyricsAutoScrollBehavior : Behavior<LyricsLineControl>
    {
        private static readonly ScrollingScrollOptions _scrollOptions = new(
            ScrollingAnimationMode.Enabled,
            ScrollingSnapPointsMode.Default);

        #region 附加属性定义

        // 目标 ScrollView 附加属性
        public static readonly DependencyProperty TargetScrollViewProperty =
            DependencyProperty.RegisterAttached(
                "TargetScrollView",
                typeof(ScrollView),
                typeof(LyricsAutoScrollBehavior),
                new PropertyMetadata(null));

        public static void SetTargetScrollView(DependencyObject element, ScrollView value)
        {
            element.SetValue(TargetScrollViewProperty, value);
        }

        public static ScrollView GetTargetScrollView(DependencyObject element)
        {
            return (ScrollView)element.GetValue(TargetScrollViewProperty);
        }

        #endregion

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null)
            {
                AssociatedObject.IsCurrentLineEvent += OnIsCurrentLineEvent;
            }
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (AssociatedObject != null)
            {
                AssociatedObject.IsCurrentLineEvent -= OnIsCurrentLineEvent;
            }
        }

        private void OnIsCurrentLineEvent(object sender, RoutedEventArgs e)
        {
            if (AssociatedObject == null)
                return;

            var lyricsLineControl = AssociatedObject;
            var container = ToolUtils.FindParent<ListViewItem>(lyricsLineControl);

            if (container == null)
                return;

            // 从 ListView 上获取附加属性
            var listView = ToolUtils.FindParent<ListView>(container);
            if (listView == null)
                return;

            var targetScrollView = GetTargetScrollView(listView);
            if (targetScrollView == null)
                return;

            try
            {
                var transform = container.TransformToVisual(targetScrollView.Content as UIElement);
                var targetPoint = transform.TransformPoint(new Point(0, 0));

                double targetOffset = targetPoint.Y
                    - (targetScrollView.ActualHeight / 2)
                    + (container.ActualHeight / 2);

                targetScrollView.ScrollTo(0, targetOffset, _scrollOptions);
            }
            catch (OperationCanceledException)
            {
                // 预期的异常，忽略
            }
            catch
            {
                // 其他异常也忽略，避免影响用户体验
            }
        }
    }
}
