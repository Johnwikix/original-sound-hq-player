using System.Numerics;
using System.Security.Cryptography;
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.UI;
using Windows.Foundation;

/// <summary>Compares original Win2D projections with raw ABI operations on real GPU targets.</summary>
internal static class NativeDrawingChecks
{
    internal static readonly List<string> Results = [];

    internal static void Run(ICanvasResourceCreator creator)
    {
        using var image = new CanvasCommandList(creator);
        using (var drawing = image.CreateDrawingSession())
        {
            drawing.FillRoundedRectangle(new Rect(0, 0, 70, 30), 7, 5, Colors.CornflowerBlue);
            drawing.DrawText("ABI 中文", 3, 2, Colors.White);
        }
        using var opacity = new CanvasLinearGradientBrush(creator, Colors.White, Colors.Transparent)
        {
            StartPoint = new(0, 0), EndPoint = new(240, 160)
        };
        foreach (float dpi in new[] { 96f, 144f })
        {
            using var expected = new CanvasRenderTarget(creator, 240, 160, dpi);
            using var actual = new CanvasRenderTarget(creator, 240, 160, dpi);
            using (var ds = expected.CreateDrawingSession())
            {
                ds.Clear(Colors.DarkSlateBlue);
                ds.Transform = Matrix3x2.CreateScale(0.9f, 1.1f) * Matrix3x2.CreateTranslation(3.5f, 7.25f);
                using (ds.CreateLayer(0.6f, new Rect(4, 5, 180, 120)))
                {
                    ds.FillRectangle(new Rect(9, 12, 140, 70), Colors.OrangeRed);
                    ds.FillRoundedRectangle(new Rect(15, 18, 100, 45), 9, 6, Colors.LightGreen);
                    ds.DrawImage(image);
                    ds.DrawImage(image, new Vector2(8, 40));
                    ds.DrawImage(image, new Rect(25, 35, 90, 35), new Rect(1, 2, 60, 25));
                    ds.DrawImage(image, new Rect(40, 65, 120, 60), new Rect(0, 0, 65, 25), 0.7f);
                }
                using (ds.CreateLayer(opacity)) ds.DrawImage(image, new Vector2(90, 90));
                ds.Transform = Matrix3x2.Identity;
                using (ds.CreateLayer(0.3f)) ds.DrawImage(image, new Vector2(150, 120));
                ds.Flush();
            }
            using (var scope = CanvasDrawingSessionScope.Create(actual))
            {
                var ds = scope.Context;
                ds.Clear(Colors.DarkSlateBlue);
                var transform = Matrix3x2.CreateScale(0.9f, 1.1f) * Matrix3x2.CreateTranslation(3.5f, 7.25f);
                ds.Transform = transform;
                if (ds.Transform != transform) throw new InvalidOperationException("ABI matrix layout mismatch.");
                if (ds.Dpi != dpi) throw new InvalidOperationException("ABI session DPI mismatch.");
                using (ds.CreateLayer(0.6f, new Rect(4, 5, 180, 120)))
                {
                    ds.FillRectangle(new Rect(9, 12, 140, 70), Colors.OrangeRed);
                    ds.FillRoundedRectangle(new Rect(15, 18, 100, 45), 9, 6, Colors.LightGreen);
                    ds.DrawImage(image);
                    ds.DrawImage(image, new Vector2(8, 40));
                    ds.DrawImage(image, new Rect(25, 35, 90, 35), new Rect(1, 2, 60, 25));
                    ds.DrawImage(image, new Rect(40, 65, 120, 60), new Rect(0, 0, 65, 25), 0.7f);
                }
                using (ds.CreateLayer(opacity)) ds.DrawImage(image, new Vector2(90, 90));
                ds.Transform = Matrix3x2.Identity;
                using (ds.CreateLayer(0.3f)) ds.DrawImage(image, new Vector2(150, 120));
                ds.Flush();
            }
            byte[] expectedPixels = expected.GetPixelBytes();
            byte[] actualPixels = actual.GetPixelBytes();
            if (!expectedPixels.AsSpan().SequenceEqual(actualPixels))
                throw new InvalidOperationException($"ABI drawing differs from original Win2D pixels at {dpi} DPI.");
            Results.Add($"{dpi} DPI: identical pixels {Convert.ToHexString(SHA256.HashData(actualPixels))}");
        }
    }
}
