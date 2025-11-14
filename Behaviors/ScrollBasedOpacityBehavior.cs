using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.Xaml.Interactivity;
using System;
using System.Diagnostics;
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

        // 缓存的变量
        private ItemsStackPanel _cachedPanel;
        private UIElement _cachedScrollContent;
        private double _cachedMaxDistance = 0;
        private double _cached0pacityRange = 0.59;

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject is not null)
            {
                AssociatedObject.ViewChanged += OnViewChanged;
                AssociatedObject.SizeChanged += OnSizeChanged;
            }
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (AssociatedObject is not null)
            {
                AssociatedObject.ViewChanged -= OnViewChanged;
                AssociatedObject.SizeChanged -= OnSizeChanged;
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCachedValues();
        }

        private void OnViewChanged(ScrollView sender, object args)
        {
            UpdateOpacity();
        }

        private void UpdateCachedValues()
        {
            if (AssociatedObject is null || TargetListView is null) return;
            _cachedPanel = TargetListView.ItemsPanelRoot as ItemsStackPanel;
            _cachedScrollContent = AssociatedObject.Content as UIElement;
            _cachedMaxDistance = AssociatedObject.ActualHeight / MaxDistanceRatio;
            _cached0pacityRange = MaxOpacity - MinOpacity;
        }

        private void UpdateOpacity()
        {
            if (AssociatedObject is null || TargetListView is null || _cachedPanel is null || _cachedScrollContent is null) return;
            double viewerCenter = AssociatedObject.VerticalOffset + (AssociatedObject.ActualHeight * 0.5);
            for (int i = _cachedPanel.FirstVisibleIndex; i <= _cachedPanel.LastVisibleIndex; i++)
            {
                var itemContainer = TargetListView.ContainerFromIndex(i) as ListViewItem;
                if (itemContainer == null) continue;

                var transform = itemContainer.TransformToVisual(_cachedScrollContent);
                var itemTop = transform.TransformPoint(default).Y;
                double itemCenter = itemTop + (itemContainer.ActualHeight * 0.5);
                double distance = Math.Abs(itemCenter - viewerCenter);

                double opacity = distance >= _cachedMaxDistance
                    ? MinOpacity
                    : MaxOpacity - ((distance / _cachedMaxDistance) * _cached0pacityRange);
                itemContainer.Opacity = opacity;
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
