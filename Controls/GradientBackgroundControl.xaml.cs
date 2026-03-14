using ColorThief.ImageSharp.Shared;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Image = SixLabors.ImageSharp.Image;

namespace WinUIMusicPlayer.Controls;

public sealed partial class GradientBackgroundControl : UserControl
{
    // ── 依赖属性 ────────────────────────────────────────────────────────

    public static readonly DependencyProperty ImageBytesProperty =
        DependencyProperty.Register(
            nameof(ImageBytes),
            typeof(byte[]),
            typeof(GradientBackgroundControl),
            new PropertyMetadata(null, OnImageBytesChanged));

    public byte[] ImageBytes
    {
        get => (byte[])GetValue(ImageBytesProperty);
        set => SetValue(ImageBytesProperty, value);
    }

    private static void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (GradientBackgroundControl)d;
        if (ctrl._effect == null) return;

        if (e.NewValue is byte[] bytes && bytes.Length > 0)
            _ = ctrl.LoadImageFromBytesAsync(bytes);
        else
            ctrl.ApplyDefaultColors();
    }

    public static readonly DependencyProperty IsDarkProperty =
        DependencyProperty.Register(
            nameof(IsDark),
            typeof(bool),
            typeof(GradientBackgroundControl),
            new PropertyMetadata(true, OnColorParamChanged));

    public bool IsDark
    {
        get => (bool)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    private static void OnColorParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (GradientBackgroundControl)d;
        if (ctrl._effect == null) return;

        if (ctrl.ImageBytes is byte[] bytes && bytes.Length > 0)
            _ = ctrl.LoadImageFromBytesAsync(bytes);
        else
            ctrl.ApplyDefaultColors();
    }

    // ── 私有字段 ────────────────────────────────────────────────────────

    private PixelShaderEffect _effect;
    private float _width;
    private float _height;
    private float _time;

    private Vector3 _c1, _c2, _c3, _c4;
    private Vector3 _target1, _target2, _target3, _target4;
    private float _transitionProgress = 1f;
    private const float TransitionSpeed = 0.677f;

    private CancellationTokenSource _loadCts;

    // ColorThief 无状态，复用同一实例避免重复构造
    private static readonly ColorThief.ImageSharp.ColorThief ColorThiefInstance = new();

    // ── 构造函数 ────────────────────────────────────────────────────────

    public GradientBackgroundControl()
    {
        InitializeComponent();
        RegisterCanvasEvents();
        canvas.SizeChanged += OnCanvasSizeChanged;
        Unloaded += OnUnloaded;
    }

    // ── 公开方法 ────────────────────────────────────────────────────────

    public async Task LoadImageAsync(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0) return;
        await LoadImageFromBytesAsync(imageBytes);
    }

    // ── 私有：注册 Canvas 事件 ──────────────────────────────────────────

    private void RegisterCanvasEvents()
    {
        canvas.CreateResources += async (s, e) =>
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "Shaders", "effect.bin");

            byte[] shaderBytes;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                           FileShare.Read, bufferSize: 4096, useAsync: true))
            {
                shaderBytes = new byte[fs.Length];
                await fs.ReadAsync(shaderBytes, 0, shaderBytes.Length);
            }

            _effect = new PixelShaderEffect(shaderBytes);
            shaderBytes = null;

            if (ImageBytes is { Length: > 0 } bytes)
            {
                await LoadImageFromBytesAsync(bytes);
            }
            else
            {
                ApplyDefaultColors();
            }

            // 首次直接应用目标色，无需过渡动画
            _c1 = _target1; _c2 = _target2;
            _c3 = _target3; _c4 = _target4;
            _transitionProgress = 1f;

            ApplyEffectProperties();
        };

        canvas.Update += (s, e) =>
        {
            if (_effect == null) return;

            _time = (float)e.Timing.TotalTime.TotalSeconds;
            _effect.Properties["iTime"] = _time;

            if (_transitionProgress >= 1f) return;

            float delta = (float)e.Timing.ElapsedTime.TotalSeconds;
            _transitionProgress = Math.Min(1f, _transitionProgress + delta * TransitionSpeed);

            float t = _transitionProgress * _transitionProgress * (3f - 2f * _transitionProgress);

            _c1 = Vector3.Lerp(_c1, _target1, t);
            _c2 = Vector3.Lerp(_c2, _target2, t);
            _c3 = Vector3.Lerp(_c3, _target3, t);
            _c4 = Vector3.Lerp(_c4, _target4, t);

            _effect.Properties["color1"] = _c1;
            _effect.Properties["color2"] = _c2;
            _effect.Properties["color3"] = _c3;
            _effect.Properties["color4"] = _c4;
        };

        canvas.Draw += (s, e) =>
        {
            if (_effect == null) return;
            e.DrawingSession.DrawImage(_effect);
        };
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _width = (float)canvas.ActualWidth;
        _height = (float)canvas.ActualHeight;
        _effect?.Properties["iResolution"] = new Vector2(_width, _height);
    }

    private void ApplyDefaultColors()
    {
        bool isDark = IsDark;
        _target1 = isDark ? new Vector3(0.08f, 0.08f, 0.10f) : new Vector3(0.92f, 0.92f, 0.95f);
        _target2 = isDark ? new Vector3(0.10f, 0.10f, 0.14f) : new Vector3(0.88f, 0.90f, 0.93f);
        _target3 = isDark ? new Vector3(0.07f, 0.09f, 0.12f) : new Vector3(0.90f, 0.91f, 0.94f);
        _target4 = isDark ? new Vector3(0.06f, 0.07f, 0.10f) : new Vector3(0.85f, 0.88f, 0.92f);
        _transitionProgress = 0f;
    }

    private void ApplyEffectProperties()
    {
        if (_effect == null) return;
        _effect.Properties["iResolution"] = new Vector2(_width, _height);
        _effect.Properties["iTime"] = _time;
        _effect.Properties["color1"] = _c1;
        _effect.Properties["color2"] = _c2;
        _effect.Properties["color3"] = _c3;
        _effect.Properties["color4"] = _c4;
    }

    // ── 私有：图片加载与颜色提取 ────────────────────────────────────────

    private async Task LoadImageFromBytesAsync(byte[] imageBytes)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        // 在 UI 线程提前读取 DependencyProperty，避免后台线程访问引发 COMException
        bool isDark = IsDark;

        try
        {
            // 返回 (颜色向量, Population) 列表，最多 4 项，按权重降序
            var weighted = await Task.Run(async () =>
            {
                using var memStream = new MemoryStream(imageBytes, writable: false);
                using var rasStream = memStream.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(rasStream);

                List<(Vector3 Color, int Population)> result;
                using (var image = await ConvertToImageSharpAsync(decoder))
                {
                    cts.Token.ThrowIfCancellationRequested();

                    var palette = ColorThiefInstance.GetPalette(image, 8, 10, isDark);

                    int totalPop = palette.Sum(t => t.Population);

                    var allByWeight = palette
                        .OrderByDescending(t => t.Population)
                        .ToList();

                    // 符合明暗要求的颜色占总像素的比例
                    int preferredPop = allByWeight
                        .Where(t => t.IsDark == isDark)
                        .Sum(t => t.Population);
                    float preferredRatio = totalPop > 0 ? (float)preferredPop / totalPop : 0f;

                    // 主色调符合时优先排符合的；否则按全图权重（EnforceLuminance 后续校正亮度）
                    IEnumerable<QuantizedColor> candidates = preferredRatio >= 0.5f
                        ? allByWeight.Where(t => t.IsDark == isDark)
                              .Concat(allByWeight.Where(t => t.IsDark != isDark))
                        : allByWeight;

                    result = candidates
                        .Take(4)
                        .Select(t => (
                            Color: new Vector3(t.Color.R / 255f, t.Color.G / 255f, t.Color.B / 255f),
                            t.Population))
                        .ToList();
                }

                return result;

            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            // 兜底：真正没有任何颜色时
            if (weighted.Count == 0)
                weighted.Add((isDark ? new Vector3(0.05f) : new Vector3(0.95f), 1));

            // 整体等比缩放调色板亮度到目标区间，保留颜色间相对亮度关系
            ScalePaletteLuminance(weighted, isDark);

            // 亮度压缩后可能导致原本不同的颜色看起来一样，做最小差异保证
            EnsureColorDiversity(weighted, isDark);

            // 按 Population 权重将颜色分配到 4 个 slot
            var slots = DistributeByPopulation(weighted);

            _target1 = slots[0];
            _target2 = slots[1];
            _target3 = slots[2];
            _target4 = slots[3];
            _transitionProgress = 0f;
        }
        catch (OperationCanceledException)
        {
            // 正常取消，忽略
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                cts.Dispose();
                _loadCts = null;
            }
        }
    }

    /// <summary>
    /// 按 Population 比例将颜色分配到恰好 4 个 slot，最后随机打乱。
    /// 例如：黑80% 红15% 白5% → [黑, 黑, 黑, 红] 打乱
    /// 规则：剩余 slot 按比例余数从大到小补给；保证不全相同。
    /// </summary>
    private static Vector3[] DistributeByPopulation(List<(Vector3 Color, int Population)> weighted)
    {
        const int Total = 4;
        int count = weighted.Count;
        int totalPop = weighted.Sum(w => w.Population);

        // 按比例分配 slot（向下取整 + 余数竞争）
        int[] slots = new int[count];
        int assigned = 0;
        for (int i = 0; i < count; i++)
        {
            slots[i] = (int)Math.Floor((float)weighted[i].Population / totalPop * Total);
            assigned += slots[i];
        }
        // 剩余 slot 逐个分给余数最大的颜色，零 LINQ 分配
        int remaining = Total - assigned;
        for (int r = 0; r < remaining; r++)
        {
            int best = 0;
            float bestRem = -1f;
            for (int i = 0; i < count; i++)
            {
                float rem = (float)weighted[i].Population / totalPop * Total - slots[i];
                if (rem > bestRem) { bestRem = rem; best = i; }
            }
            slots[best]++;
        }

        // 保证不全相同：主色独占全部 4 个时让出 1 个给次色
        if (count >= 2 && slots[0] == Total)
        {
            slots[0] = Total - 1;
            slots[1] = 1;
        }
        else if (count == 1)
        {
            var nudge = Vector3.Clamp(
                weighted[0].Color + new Vector3(0.05f, -0.03f, 0.04f),
                new Vector3(0.01f), new Vector3(0.99f));
            weighted.Add((nudge, 0));
            slots = new[] { Total - 1, 1 };
            count = 2;
        }

        // 展开到栈上固定数组，Fisher-Yates 随机打乱，零额外分配
        var result = new Vector3[Total];
        int idx = 0;
        for (int i = 0; i < count && idx < Total; i++)
            for (int j = 0; j < slots[i] && idx < Total; j++)
                result[idx++] = weighted[i].Color;

        var rng = new Random();
        for (int i = Total - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
    }

    /// <summary>
    /// 检查颜色列表中两两之间的感知距离，若小于阈值则对较次要的颜色做亮度微分离，
    /// 保证每对颜色在视觉上可区分，避免 EnforceLuminance 压缩后出现"只有一种颜色"的感觉。
    /// </summary>
    private static void EnsureColorDiversity(List<(Vector3 Color, int Population)> weighted, bool isDark)
    {
        const float MinDistance = 0.12f; // HSL 空间欧氏距离阈值，低于此值认为视觉上过于相近
        const float LShift = 0.08f;      // 亮度微分离幅度

        float lMin = isDark ? 0.03f : 0.55f;
        float lMax = isDark ? 0.45f : 0.97f;

        for (int i = 0; i < weighted.Count; i++)
        {
            for (int j = i + 1; j < weighted.Count; j++)
            {
                RgbToHsl(weighted[i].Color, out float hi, out float si, out float li);
                RgbToHsl(weighted[j].Color, out float hj, out float sj, out float lj);

                // 色相距离取环形最短路径
                float dh = Math.Abs(hi - hj);
                if (dh > 0.5f) dh = 1f - dh;
                float ds = si - sj;
                float dl = li - lj;
                float dist = MathF.Sqrt(dh * dh + ds * ds + dl * dl);

                if (dist >= MinDistance) continue;

                // j 是次要颜色（Population 较小），对它做亮度偏移使其与 i 拉开距离
                // 偏移方向：若 j 亮度 >= i，则把 j 再推亮；否则把 j 推暗
                float newLj = lj >= li
                    ? Math.Min(lMax, lj + LShift)
                    : Math.Max(lMin, lj - LShift);

                if (Math.Abs(newLj - lj) > 1e-4f)
                    weighted[j] = (HslToRgb(hj, sj, newLj), weighted[j].Population);
            }
        }
    }

    /// <summary>
    /// 对整个调色板做整体亮度等比缩放，保留颜色间相对亮度关系。
    ///
    /// 算法：
    ///   1. 找出调色板中最亮（maxL）和最暗（minL）的颜色
    ///   2. 若整体已在目标区间内，不做处理
    ///   3. 否则将 [minL, maxL] 线性映射到目标区间 [targetMin, targetMax]
    ///      每个颜色的新亮度 = targetMin + (l - minL) / (maxL - minL) * (targetMax - targetMin)
    ///   4. 若所有颜色亮度相同（扁平调色板），整体平移到目标区间中点
    ///
    /// isDark=true  → 目标区间 [0.05, 0.45]
    /// isDark=false → 目标区间 [0.55, 0.95]
    /// </summary>
    private static void ScalePaletteLuminance(List<(Vector3 Color, int Population)> weighted, bool isDark)
    {
        // dark  模式：最亮色不超过 0.45
        // light 模式：最暗色不低于 0.55
        // 若已满足则完全不动，贴近原图；否则做最小幅度的线性映射：
        //   - 先将超出的一端锚定到边界
        //   - 另一端按原始亮度范围等比收缩，保留颜色间相对亮度差距
        //   - 不强制拉满整个目标区间，只做刚好够的调整
        float hardLimit = isDark ? 0.45f : 0.55f;

        int count = weighted.Count;
        Span<float> hs = stackalloc float[count];
        Span<float> ss = stackalloc float[count];
        Span<float> ls = stackalloc float[count];

        float minL = float.MaxValue, maxL = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            RgbToHsl(weighted[i].Color, out hs[i], out ss[i], out ls[i]);
            if (ls[i] < minL) minL = ls[i];
            if (ls[i] > maxL) maxL = ls[i];
        }

        // 整体已满足，不动
        if (isDark && maxL <= hardLimit) return;
        if (!isDark && minL >= hardLimit) return;

        float srcRange = maxL - minL;

        for (int i = 0; i < count; i++)
        {
            float newL;
            if (srcRange < 1e-5f)
            {
                // 扁平调色板：直接平移到边界
                newL = hardLimit;
            }
            else if (isDark)
            {
                // 锚定 maxL → hardLimit，其余等比收缩
                // newL = hardLimit - (maxL - ls[i]) / srcRange * srcRange
                //       但 srcRange 两端都要缩进 [0.01, hardLimit]
                // 新区间高端 = hardLimit，低端 = hardLimit - srcRange（若 < 0.01 则截）
                float newMin = Math.Max(0.01f, hardLimit - srcRange);
                float newRange = hardLimit - newMin;
                newL = newMin + (ls[i] - minL) / srcRange * newRange;
            }
            else
            {
                // 锚定 minL → hardLimit，其余等比收缩
                float newMax = Math.Min(0.99f, hardLimit + srcRange);
                float newRange = newMax - hardLimit;
                newL = hardLimit + (ls[i] - minL) / srcRange * newRange;
            }

            weighted[i] = (HslToRgb(hs[i], ss[i], Math.Clamp(newL, 0.01f, 0.99f)), weighted[i].Population);
        }
    }

    private static void RgbToHsl(Vector3 rgb, out float h, out float s, out float l)
    {
        float r = rgb.X, g = rgb.Y, b = rgb.Z;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        l = (max + min) * 0.5f;

        if (delta < 1e-5f) { h = 0f; s = 0f; return; }

        s = l > 0.5f ? delta / (2f - max - min) : delta / (max + min);

        if (max == r) h = ((g - b) / delta + (g < b ? 6f : 0f)) / 6f;
        else if (max == g) h = ((b - r) / delta + 2f) / 6f;
        else h = ((r - g) / delta + 4f) / 6f;
    }

    private static Vector3 HslToRgb(float h, float s, float l)
    {
        if (s < 1e-5f) return new Vector3(l);

        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;

        return new Vector3(
            HueToRgb(p, q, h + 1f / 3f),
            HueToRgb(p, q, h),
            HueToRgb(p, q, h - 1f / 3f));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    private static async Task<Image<Rgba32>> ConvertToImageSharpAsync(BitmapDecoder decoder)
    {
        const uint MaxSize = 150;
        uint srcWidth = decoder.PixelWidth;
        uint srcHeight = decoder.PixelHeight;

        float scale = Math.Min(1f, Math.Min((float)MaxSize / srcWidth, (float)MaxSize / srcHeight));
        uint targetWidth = Math.Max(1, (uint)(srcWidth * scale));
        uint targetHeight = Math.Max(1, (uint)(srcHeight * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = targetWidth,
            ScaledHeight = targetHeight,
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = pixelData.DetachPixelData();
        var image = Image.LoadPixelData<Rgba32>(pixels, (int)targetWidth, (int)targetHeight);
        pixels = null;
        return image;
    }

    // ── 资源释放 ────────────────────────────────────────────────────────

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _effect?.Dispose();
        _effect = null;
    }
}