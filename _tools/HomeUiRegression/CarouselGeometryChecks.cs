using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class HomePolishWindow
{
    private async Task CheckCarouselEdges()
    {
        // Let UIA finish validating the invoked element before resizing its test window.
        await Task.Delay(500);
        var originalSize = AppWindow.Size;
        var measurements = new List<object>();
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
            foreach (int count in new[] { 0, 1, 2, 3 })
            {
                carousel.ThumbnailItemsSource = Enumerable.Range(1, count).Cast<object>().ToArray();
                foreach (double width in new[] { 320d, 600d, 900d, 1280d, 1820d, 2460d })
                {
                    double scale = _root.XamlRoot.RasterizationScale;
                    AppWindow.Resize(new SizeInt32((int)((width + 96) * scale), (int)((width * 9 / 16 + 200) * scale)));
                    viewport.Width = width + 48;
                    viewport.Height = width * 9 / 16 + 48;
                    viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, viewport.Width, viewport.Height) };
                    await Wait(() => carousel.IsLoaded && Math.Abs(((Grid)carousel.FindName("LayoutGrid")).ActualWidth - width) < 1);
                    viewport.UpdateLayout();
                    await Task.Delay(50);
                    carousel.Clip = new RectangleGeometry { Rect = new Rect(0, 0, carousel.ActualWidth, carousel.ActualHeight) };
                    typeof(FeaturedCarousel).GetMethod("Carousel_PointerEntered", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(carousel, [null, null]);
                    var slider = (Grid)carousel.FindName("SliderGrid");
                    var collage = (Grid)carousel.FindName("ThumbnailGrid");
                    var left = (Button)carousel.FindName("PreviousButton");
                    var right = (Button)carousel.FindName("NextButton");
                    double leftX = left.TransformToVisual(slider).TransformPoint(new Point()).X;
                    double rightEdge = right.TransformToVisual(slider).TransformPoint(new Point()).X + right.ActualWidth;
                    double imageX = slider.TransformToVisual(viewport).TransformPoint(new Point()).X;
                    CheckRatio(slider, $"main at {width}, count={count}");
                    Assert(Math.Abs(imageX - 24) < .1 && Math.Abs(leftX + 10) < .1
                        && Math.Abs(rightEdge - slider.ActualWidth - 10) < .1,
                        $"outer gutters and outward arrow positions remain intact at {width} DIP, count={count}");
                    if (count == 0)
                        Assert(Math.Abs(slider.ActualWidth - width) < 1.25, "no-thumbnail layout uses the full width at " + width);
                    else
                    {
                        var primary = (ContentPresenter)carousel.FindName("PrimaryThumbnail");
                        var secondary = (ContentPresenter)carousel.FindName("SecondaryThumbnail");
                        var tertiary = (ContentPresenter)carousel.FindName("TertiaryThumbnail");
                        CheckRatio(primary, "primary at " + width);
                        if (count >= 2) CheckRatio(secondary, "secondary at " + width);
                        if (count >= 3) CheckRatio(tertiary, "tertiary at " + width);
                        Assert(Math.Abs(collage.ActualHeight - slider.ActualHeight) < 1.25
                            && Math.Abs(slider.ActualWidth + collage.ActualWidth + 15 - width) < 1.25,
                            "all visible cards align as a single band at " + width);
                        var last = count == 1 ? primary : secondary;
                        double bottom = last.TransformToVisual(collage).TransformPoint(new Point()).Y + last.ActualHeight;
                        Assert(Math.Abs(bottom - slider.ActualHeight) < 1.25, "collage bottom aligns with the main image at " + width);
                    }
                    measurements.Add(new { Width = width, Count = count, MainWidth = slider.ActualWidth,
                        MainHeight = slider.ActualHeight, CollageWidth = collage.ActualWidth, CollageHeight = collage.ActualHeight });
                    foreach (var button in new[] { left, right })
                        foreach (double x in new[] { 3d, 16d, 29d })
                        {
                            var point = button.TransformToVisual(_root).TransformPoint(new Point(x, 16));
                            Assert(VisualTreeHelper.FindElementsInHostCoordinates(point, _root).Contains(button),
                                $"unclipped arrow hit surface at {width} DIP, count={count}, button={button.Name}, x={x}");
                        }
                }
            }
        }
        finally
        {
            _root.Children.Remove(viewport);
            AppWindow.Resize(originalSize);
            File.WriteAllText(Path.Combine(_output, "carousel-16x9-layout.json"), JsonSerializer.Serialize(measurements));
        }
        try
        {
            foreach (var size in new[] { new SizeInt32(1280, 720), new SizeInt32(1920, 1080), new SizeInt32(2560, 1440) })
            {
                AppWindow.Resize(size);
                await Task.Delay(250);
                _page!.UpdateLayout();
                ((ScrollViewer)_page.FindName("ContentScroll")).ChangeView(null, 0, null, true);
                await Task.Delay(200);
                var featured = (FeaturedCarousel)_page.FindName("Featured");
                CheckRatio((Grid)featured.FindName("SliderGrid"), "actual home at " + size.Width);
                // The missing-artwork regression deliberately left the hero without artwork; restore its real theme here.
                var hero = (HomeHeroCard)_page.FindName("HomeHero");
                var heroCover = (MusicCoverControl)hero.FindName("Artwork");
                var themeArtwork = hero.Theme?.Artwork ?? _songs[0];
                if (!ReferenceEquals(heroCover.Music, themeArtwork)) heroCover.Music = themeArtwork;
                var covers = Descendants<MusicCoverControl>(featured).ToArray();
                await Wait(() => covers.All(cover => ((Image)cover.FindName("CoverImage")).Source is not null));
                Assert(covers.Length == 4, "centered cropping checks all four loaded native images at " + size.Width);
                foreach (var cover in covers)
                {
                    var image = (Image)cover.FindName("CoverImage");
                    var offset = image.TransformToVisual(cover).TransformPoint(new Point());
                    Assert(image.Stretch == Stretch.UniformToFill && image.HorizontalAlignment == HorizontalAlignment.Center
                        && image.VerticalAlignment == VerticalAlignment.Center
                        && Math.Abs(offset.X + image.ActualWidth / 2 - cover.ActualWidth / 2) < 1
                        && Math.Abs(offset.Y + image.ActualHeight / 2 - cover.ActualHeight / 2) < 1,
                        "native home artwork remains centered with uniform cropping at " + size.Width);
                }
                await WindowCapture.SaveAsync(WindowCapture.ReadFrame(this), Path.Combine(_output, $"carousel-16x9-{size.Width}.png"));
            }
        }
        finally { AppWindow.Resize(originalSize); }
    }
    private void CheckRatio(FrameworkElement element, string label)
        => Assert(element.ActualWidth > 0 && element.ActualHeight > 0
            && Math.Abs(element.ActualHeight - element.ActualWidth * 9 / 16) < 1.25,
            $"16:9 artwork {label}: {element.ActualWidth:F1}x{element.ActualHeight:F1}");
}
