using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.Text;
using Windows.Foundation;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Behaviors
{
    public class AnimatedLyricsAutoScrollBehavior : Behavior<AnimatedLyricsLineControl>
    {
        private static readonly ScrollingScrollOptions _scrollOptions = new(
            ScrollingAnimationMode.Enabled,
            ScrollingSnapPointsMode.Default);

        #region 附加属性

        public static readonly DependencyProperty TargetScrollViewProperty =
            DependencyProperty.RegisterAttached(
                "TargetScrollView",
                typeof(ScrollView),
                typeof(AnimatedLyricsAutoScrollBehavior),
                new PropertyMetadata(null));

        public static void SetTargetScrollView(DependencyObject element, ScrollView value)
            => element.SetValue(TargetScrollViewProperty, value);

        public static ScrollView GetTargetScrollView(DependencyObject element)
            => (ScrollView)element.GetValue(TargetScrollViewProperty);

        #endregion

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject is not null)
                AssociatedObject.IsCurrentLineChanged += OnIsCurrentLineChanged;
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (AssociatedObject is not null)
                AssociatedObject.IsCurrentLineChanged -= OnIsCurrentLineChanged;
        }

        private void OnIsCurrentLineChanged(object sender, bool isCurrentLine)
        {
            if (!isCurrentLine) return;
            if (AssociatedObject is null) return;

            var container = ToolUtils.FindParent<ListViewItem>(AssociatedObject);
            if (container is null) return;

            var listView = ToolUtils.FindParent<ListView>(container);
            if (listView is null) return;

            var targetScrollView = GetTargetScrollView(listView);
            if (targetScrollView is null) return;

            try
            {
                var transform = container.TransformToVisual(targetScrollView.Content as UIElement);
                var targetPoint = transform.TransformPoint(new Point(0, 0));

                double targetOffset = targetPoint.Y
                    - targetScrollView.ActualHeight / 2
                    + container.ActualHeight / 2;

                targetScrollView.ScrollTo(0, targetOffset, _scrollOptions);
            }
            catch (OperationCanceledException) { }
            catch { }
        }
    }
}
