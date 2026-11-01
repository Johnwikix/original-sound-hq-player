using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class HomePolishWindow
{
    private async Task CheckHoverFrames()
    {
        await PrepareHoverCheck();
        var preview = (HomeSongPreview)_page!.FindName("SongPreview");
        var source = (FrameworkElement)typeof(HomePage).GetField("_previewSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_page)!;
        var cover = Descendants<MusicCoverControl>(source).First();
        var previewCover = (MusicCoverControl)preview.FindName("Artwork");
        await Wait(() => ((Image)previewCover.FindName("CoverImage")).Source is not null
            && ((Image)cover.FindName("CoverImage")).Source is not null);
        var bounds = cover.TransformToVisual(_root).TransformBounds(new Rect(0, 0, cover.ActualWidth, cover.ActualHeight));
        var sourceImage = (Image)cover.FindName("CoverImage");
        var largeImage = (Image)previewCover.FindName("CoverImage");
        File.WriteAllText(Path.Combine(_output, "hover-layout.json"), JsonSerializer.Serialize(new {
            Bounds = bounds, Source = new { cover.ActualWidth, cover.ActualHeight, ImageWidth = sourceImage.ActualWidth, ImageHeight = sourceImage.ActualHeight },
            Preview = new { preview.ActualWidth, preview.ActualHeight, CoverWidth = previewCover.ActualWidth, CoverHeight = previewCover.ActualHeight,
                ImageWidth = largeImage.ActualWidth, ImageHeight = largeImage.ActualHeight } }));
        var expanded = WindowCapture.ReadFrame(this);
        await WindowCapture.SaveAsync(expanded, Path.Combine(_output, "hover-expanded.png"));
        var frames = new List<(double Time, bool Closed, WindowCapture.Frame Frame)>();
        var watch = Stopwatch.StartNew();
        typeof(HomePage).GetMethod("BeginPreviewClose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_page, null);
        var visual = ElementCompositionPreview.GetElementVisual(preview);
        var clip = (InsetClip)visual.Clip;
        var controllers = new[] { visual.TryGetAnimationController("Scale"), visual.TryGetAnimationController("Translation"),
            visual.TryGetAnimationController("Opacity"), clip.TryGetAnimationController("BottomInset") };
        // Slow only this native animation instance for frame capture, without changing product timing or its curve.
        foreach (var controller in controllers) if (controller is not null) controller.PlaybackRate = .2f;
        try
        {
            int closedFrames = 0;
            for (int i = 0; i < 90; i++)
            {
                var frame = WindowCapture.ReadFrame(this);
                double scale = _root.XamlRoot.RasterizationScale;
                int x = Math.Clamp((int)Math.Round(bounds.X * scale) - 10, 0, frame.Width - 1);
                int y = Math.Clamp((int)Math.Round(bounds.Y * scale) - 10, 0, frame.Height - 1);
                int width = Math.Min((int)Math.Round(bounds.Width * scale) + 20, frame.Width - x);
                int height = Math.Min((int)Math.Round(bounds.Height * scale) + 20, frame.Height - y);
                var cropped = new byte[width * height * 4];
                for (int row = 0; row < height; row++)
                    Array.Copy(frame.Pixels, ((y + row) * frame.Width + x) * 4, cropped, row * width * 4, width * 4);
                bool closed = _home.PreviewSong is null;
                frames.Add((watch.Elapsed.TotalMilliseconds, closed, new(width, height, cropped)));
                if (closed && ++closedFrames == 4) break;
                await Task.Delay(12);
            }
            var final = frames[^1].Frame;
            var metrics = new List<object>();
            var interiorSteps = new List<double>();
            for (int i = 0; i < frames.Count; i++)
            {
                long difference = 0, step = 0;
                for (int p = 0; p < final.Pixels.Length; p += 4)
                    for (int c = 0; c < 3; c++)
                    {
                        difference += Math.Abs(frames[i].Frame.Pixels[p + c] - final.Pixels[p + c]);
                        if (i > 0) step += Math.Abs(frames[i].Frame.Pixels[p + c] - frames[i - 1].Frame.Pixels[p + c]);
                    }
                long interior = 0;
                int count = 0;
                if (i > 0)
                    for (int y = 24; y < final.Height - 24; y++)
                        for (int x = 24; x < final.Width - 24; x++)
                            for (int c = 0; c < 3; c++)
                            {
                                int offset = (y * final.Width + x) * 4 + c;
                                interior += Math.Abs(frames[i].Frame.Pixels[offset] - frames[i - 1].Frame.Pixels[offset]);
                                count++;
                            }
                interiorSteps.Add(count == 0 ? 0 : interior / (double)count);
                metrics.Add(new { Index = i, frames[i].Time, frames[i].Closed,
                    Difference = difference / (final.Width * final.Height * 3.0), Step = step / (final.Width * final.Height * 3.0), InteriorStep = interiorSteps[^1] });
                await WindowCapture.SaveAsync(frames[i].Frame, Path.Combine(_output, $"hover-frame-{i:D2}.png"));
            }
            File.WriteAllText(Path.Combine(_output, "hover-frames.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
            Assert(frames.Count > 8 && frames.Any(frame => frame.Closed), "native composition frames capture the hover exit and completed handoff");
            int firstClosed = frames.FindIndex(frame => frame.Closed);
            double latePeak = interiorSteps.Skip(Math.Max(1, firstClosed - 4)).Take(5).Max();
            Assert(latePeak < 1.5, $"cover interior has no late brightness pulse through the completed handoff (peak mean RGB step={latePeak:F3}/255)");
        }
        finally
        {
            foreach (var controller in controllers) controller?.Dispose();
        }
    }
}
