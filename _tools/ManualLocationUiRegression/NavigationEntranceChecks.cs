using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

internal sealed partial class TestWindow
{
    private async Task CheckTabEntrance()
    {
        bool animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        foreach (string tag in new[] { "favourite", "album", "artist", "song" })
        {
            string otherTab = tag == "song" ? "favourite" : "song";
            await Open(otherTab);
            var item = Selector.Items.Single(item => Equals(item.Tag, tag));
            var first = await SampleEntrance(InnerFrame, () => Selector.SelectedItem = item, tag + " entry");
            await Open(otherTab);
            var second = await SampleEntrance(InnerFrame, () => Selector.SelectedItem = item, tag + " cached entry");
            Check(ReferenceEquals(first.Page, second.Page), tag + " reuses the same page when selected again");
            if (animationsEnabled)
            {
                Check(first.Animated && second.Animated, tag + " shows an entrance on both visits");
            }
        }

        var browser = _shell.CurrentLibraryPage;
        _shell.NavigateToFoldersPage();
        await Task.Delay(400);
        var returned = await SampleEntrance(_shell.ShellFrame, _shell.NavigateToMusicBrowsePage, "library cached entry");
        Check(ReferenceEquals(browser, returned.Page), "sidebar return reuses the browser");
        if (animationsEnabled)
        {
            Check(returned.Animated, "sidebar return animates the cached browser");
        }
    }

    private async Task<(Page Page, bool Animated)> SampleEntrance(Frame frame, Action navigate, string label)
    {
        var marker = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 250, 20, 250)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(40, 28, 0, 0),
            Width = 80,
            Height = 18,
            IsHitTestVisible = false
        };
        Panel? panel = null;
        void AddMarker(object sender, NavigationEventArgs args)
        {
            panel = (Panel)((Page)args.Content).Content;
            panel.Children.Add(marker);
        }

        frame.Navigated += AddMarker;
        try
        {
            navigate();
            Check(panel is not null, label + " performs a real Frame navigation");
            var samples = new List<(int Top, int ColorContrast)>();
            for (int i = 0; i < 14; i++)
            {
                await Task.Delay(16);
                samples.Add(FindMarker(WindowCapture.ReadFrame(this)));
            }
            await Task.Delay(400);
            var settled = FindMarker(WindowCapture.ReadFrame(this));
            Check(settled.ColorContrast > 200, "marker is visible at the end of " + label);
            // Ignore frames that have not presented the incoming page yet.
            bool animated = samples.Any(sample => sample.ColorContrast > 24 &&
                (sample.Top > settled.Top + 2 || sample.ColorContrast < settled.ColorContrast - 18));
            _measurements.Add(new { Label = label, Animated = animated, SettledTop = settled.Top,
                Samples = samples.Select(sample => new { sample.Top, sample.ColorContrast }).ToArray() });
            return ((Page)frame.Content, animated);
        }
        finally
        {
            frame.Navigated -= AddMarker;
            panel?.Children.Remove(marker);
        }
    }

    private static (int Top, int ColorContrast) FindMarker(WindowCapture.Frame frame)
    {
        int top = -1, contrast = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 32; x < frame.Width / 4; x += 8)
            {
                int offset = (y * frame.Width + x) * 4;
                int blue = frame.Pixels[offset], green = frame.Pixels[offset + 1], red = frame.Pixels[offset + 2];
                int difference = Math.Min(red, blue) - green;
                if (difference <= 24 || Math.Abs(red - blue) > 12) continue;
                if (top < 0) top = y;
                contrast = Math.Max(contrast, difference);
            }
        }
        return (top, contrast);
    }
}
