using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using Windows.Graphics;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Behaviors;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    private static async Task CheckPlaybackBarCoverAsync(Window window)
    {
        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        var layer = new Grid { Width = 80, Height = 80 };
        var image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        var behavior = new FadeImageBehavior { Duration = TimeSpan.FromSeconds(1) };
        Interaction.GetBehaviors(image).Add(behavior);
        layer.Children.Add(image);
        root.Children.Add(layer);
        window.Content = root;
        App.MainWindow = window;
        window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, 640, 440));
        window.Activate();
        await WaitUntilAsync(() => image.IsLoaded && image.ActualTheme == ElementTheme.Dark && image.Source is not null,
            "The dark playback bar did not load its placeholder.");
        var rendered = new RenderTargetBitmap();
        await CheckPlaceholderAsync(ElementTheme.Dark);
        Console.WriteLine("PASS dark startup uses the dark playback bar placeholder.");

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            var old = image.Source;
            root.RequestedTheme = theme;
            await WaitUntilAsync(() => image.ActualTheme == theme && image.Source is not null && !ReferenceEquals(image.Source, old),
                $"The playback bar did not refresh its placeholder for {theme}.");
            await CheckPlaceholderAsync(theme);
            if (layer.Children.Count != 1)
                throw new InvalidOperationException("A theme change animated between two placeholders.");
        }
        Console.WriteLine("PASS playback bar updates the placeholder for light and dark window themes.");

        async Task CheckPlaceholderAsync(ElementTheme theme)
        {
            await rendered.RenderAsync(image);
            byte[] pixels = (await rendered.GetPixelsAsync()).ToArray();
            int offset = (rendered.PixelWidth * 5 + 5) * 4;
            byte shade = pixels[offset];
            if (pixels[offset + 3] < 250 || (theme == ElementTheme.Dark ? shade >= 75 : shade < 175))
                throw new InvalidOperationException($"{theme} playback bar displayed placeholder pixel {shade}.");
        }
    }
}
