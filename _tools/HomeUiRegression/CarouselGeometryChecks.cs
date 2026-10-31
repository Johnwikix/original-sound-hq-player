using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class HomePolishWindow
{
    private async Task CheckCarouselEdges()
    {
        var carousel = new FeaturedCarousel { ItemsSource = _home.Themes, AutoShuffle = false,
            MainContent = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) } };
        var viewport = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var content = new StackPanel { Padding = new Thickness(24, 0, 24, 0) };
        content.Children.Add(carousel);
        viewport.Children.Add(content);
        Grid.SetRow(viewport, 1);
        _root.Children.Add(viewport);
        try
        {
            foreach (bool thumbnails in new[] { false, true })
            {
                carousel.ThumbnailItemsSource = thumbnails ? new object[] { 1, 2, 3 } : null;
                foreach (double width in new[] { 320d, 600d, 900d })
                {
                    viewport.Width = width + 48;
                    viewport.Height = width / 1.22 + 48;
                    viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, viewport.Width, viewport.Height) };
                    await Wait(() => carousel.IsLoaded && Math.Abs(((Grid)carousel.FindName("LayoutGrid")).ActualWidth - width) < 1);
                    viewport.UpdateLayout();
                    carousel.Clip = new RectangleGeometry { Rect = new Rect(0, 0, carousel.ActualWidth, carousel.ActualHeight) };
                    typeof(FeaturedCarousel).GetMethod("Carousel_PointerEntered", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(carousel, [null, null]);
                    var slider = (Grid)carousel.FindName("SliderGrid");
                    var left = (Button)carousel.FindName("PreviousButton");
                    var right = (Button)carousel.FindName("NextButton");
                    double leftX = left.TransformToVisual(slider).TransformPoint(new Point()).X;
                    double rightEdge = right.TransformToVisual(slider).TransformPoint(new Point()).X + right.ActualWidth;
                    double imageX = slider.TransformToVisual(viewport).TransformPoint(new Point()).X;
                    double expectedWidth = thumbnails ? (width - 15) * .6 : width;
                    Assert(Math.Abs(imageX - 24) < .1 && Math.Abs(slider.ActualWidth - expectedWidth) < 1
                        && Math.Abs(leftX + 10) < .1 && Math.Abs(rightEdge - slider.ActualWidth - 10) < .1,
                        $"original image and outward arrow positions are unchanged at {width} DIP, thumbnails={thumbnails}");
                    foreach (var button in new[] { left, right })
                    {
                        foreach (double x in new[] { 3d, 16d, 29d })
                        {
                            var point = button.TransformToVisual(_root).TransformPoint(new Point(x, 16));
                            Assert(VisualTreeHelper.FindElementsInHostCoordinates(point, _root).Contains(button),
                                $"unclipped arrow hit surface at {width} DIP, thumbnails={thumbnails}, button={button.Name}, x={x}");
                        }
                    }
                }
            }
        }
        finally { _root.Children.Remove(viewport); }
    }
}
