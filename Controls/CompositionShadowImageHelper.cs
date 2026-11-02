// Adapted from DevWinUI ColorHelperEx.GetImageEdgeColorWithWin2DAsync and GeneralHelper.GetUriFromObjectSource (MIT).
// See Libraries/DevWinUI/LICENSE-Controls.txt for the pinned upstream source.
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace WinUIMusicPlayer.Controls;

/// <summary>Loads a shadow image and computes the upstream perimeter average without copying the entire bitmap.</summary>
internal static class CompositionShadowImageHelper
{
    internal static async Task<Color> GetEdgeColorAsync(object? source, CancellationToken cancellationToken)
    {
        Uri? uri = source switch
        {
            Uri value => value,
            string text when Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var value) => value,
            _ => null
        };
        if (uri is null) return Colors.Black;

        using var bitmap = await LoadAsync(uri, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        int width = checked((int)bitmap.SizeInPixels.Width);
        int height = checked((int)bitmap.SizeInPixels.Height);
        if (width == 0 || height == 0) return Colors.Black;

        // 取色是冷路径；.NET 10+ Span 累加四边，保留上游重复统计角点的算法。
        // Win2D 的返回数组只覆盖边缘，不再为整张封面及 List<Color> 分配副本。
        long red = 0, green = 0, blue = 0;
        Accumulate(bitmap.GetPixelColors(0, 0, width, 1), ref red, ref green, ref blue);
        Accumulate(bitmap.GetPixelColors(0, height - 1, width, 1), ref red, ref green, ref blue);
        Accumulate(bitmap.GetPixelColors(0, 0, 1, height), ref red, ref green, ref blue);
        Accumulate(bitmap.GetPixelColors(width - 1, 0, 1, height), ref red, ref green, ref blue);
        long count = 2L * (width + height);
        return Color.FromArgb(255, (byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }

    private static async Task<CanvasBitmap> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {
        var device = CanvasDevice.GetSharedDevice();
        if (uri.IsAbsoluteUri && !uri.IsFile && uri.Scheme != "ms-appx")
            return await CanvasBitmap.LoadAsync(device, uri).AsTask(cancellationToken);

        // 用应用目录解析包内资源也支持 unpackaged 回归程序；绝对 file URI 使用 LocalPath。
        string path = !uri.IsAbsoluteUri ? Path.Combine(AppContext.BaseDirectory, uri.OriginalString)
            : uri.IsFile ? uri.LocalPath
            : Path.Combine(AppContext.BaseDirectory, Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'));
        using var stream = File.OpenRead(path);
        return await CanvasBitmap.LoadAsync(device, stream.AsRandomAccessStream()).AsTask(cancellationToken);
    }

    private static void Accumulate(ReadOnlySpan<Color> colors, ref long red, ref long green, ref long blue)
    {
        foreach (var color in colors)
        {
            red += color.R;
            green += color.G;
            blue += color.B;
        }
    }
}
