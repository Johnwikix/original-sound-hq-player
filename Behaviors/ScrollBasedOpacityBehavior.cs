using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;

namespace WinUIMusicPlayer.Behaviors
{
    public class ScrollBasedOpacityBehavior : Behavior<ScrollView>
    {
        public static readonly DependencyProperty TargetListViewProperty =
            DependencyProperty.Register(
                nameof(TargetListView),
                typeof(ListView),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata(null));

        public static readonly DependencyProperty MaxOpacityProperty =
            DependencyProperty.Register(
                nameof(MaxOpacity),
                typeof(double),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata(0.6));

        public static readonly DependencyProperty MinOpacityProperty =
            DependencyProperty.Register(
                nameof(MinOpacity),
                typeof(double),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata(0.01));

        public static readonly DependencyProperty MaxDistanceRatioProperty =
            DependencyProperty.Register(
                nameof(MaxDistanceRatio),
                typeof(double),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata(2.2));

        public static readonly DependencyProperty TargetElementNameProperty =
            DependencyProperty.Register(
                nameof(TargetElementName),
                typeof(string),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata("LyricsTextBlockBase"));

        public ListView TargetListView
        {
            get => (ListView)GetValue(TargetListViewProperty);
            set => SetValue(TargetListViewProperty, value);
        }

        public double MaxOpacity
        {
            get => (double)GetValue(MaxOpacityProperty);
            set => SetValue(MaxOpacityProperty, value);
        }

        public double MinOpacity
        {
            get => (double)GetValue(MinOpacityProperty);
            set => SetValue(MinOpacityProperty, value);
        }

        public double MaxDistanceRatio
        {
            get => (double)GetValue(MaxDistanceRatioProperty);
            set => SetValue(MaxDistanceRatioProperty, value);
        }

        public string TargetElementName
        {
            get => (string)GetValue(TargetElementNameProperty);
            set => SetValue(TargetElementNameProperty, value);
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null)
            {
                AssociatedObject.ViewChanged += OnViewChanged;
            }
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (AssociatedObject != null)
            {
                AssociatedObject.ViewChanged -= OnViewChanged;
            }
        }

        private void OnViewChanged(ScrollView sender, object args)
        {
            UpdateOpacity();
        }

        private void UpdateOpacity()
        {
            if (AssociatedObject == null || TargetListView == null) return;

            double viewerHeight = AssociatedObject.ActualHeight;
            double viewerCenter = AssociatedObject.VerticalOffset + (viewerHeight / 2);
            double maxDistance = viewerHeight / MaxDistanceRatio;
            double opacityRange = MaxOpacity - MinOpacity;

            var panel = TargetListView.ItemsPanelRoot as ItemsStackPanel;
            if (panel == null) return;

            for (int i = panel.FirstVisibleIndex; i <= panel.LastVisibleIndex; i++)
            {
                var itemContainer = TargetListView.ContainerFromIndex(i) as ListViewItem;
                if (itemContainer == null) continue;

                var targetElement = FindElementByName(itemContainer, TargetElementName) as TextBlock;
                if (targetElement == null) continue;

                var transform = itemContainer.TransformToVisual(AssociatedObject.Content as UIElement);
                var itemTop = transform.TransformPoint(new Point(0, 0)).Y;
                double itemCenter = itemTop + (itemContainer.ActualHeight / 2);
                double distance = Math.Abs(itemCenter - viewerCenter);

                double opacity = distance >= maxDistance
                    ? MinOpacity
                    : MaxOpacity - ((distance / maxDistance) * opacityRange);

                targetElement.Opacity = opacity;
            }
        }

        private FrameworkElement FindElementByName(DependencyObject parent, string name)
        {
            if (parent is FrameworkElement fe && fe.Name == name)
                return fe;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var result = FindElementByName(child, name);
                if (result != null) return result;
            }

            return null;
        }
    }
}
