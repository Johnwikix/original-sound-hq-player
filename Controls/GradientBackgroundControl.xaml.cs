using ColorThief.ImageSharp.Shared;
using Microsoft.Graphics.Canvas;
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
/// <summary>
/// 渐变流体背景，Powered by Isolation https://github.com/Storyteller-Studios/Isolation
/// </summary>
public sealed partial class GradientBackgroundControl : UserControl, IDisposable
{
    public static readonly DependencyProperty UseImageDominantThemeProperty =
    DependencyProperty.Register(
        nameof(UseImageDominantTheme),
        typeof(bool),
        typeof(GradientBackgroundControl),
        new PropertyMetadata(false, OnColorParamChanged));

    public bool UseImageDominantTheme
    {
        get => (bool)GetValue(UseImageDominantThemeProperty);
        set => SetValue(UseImageDominantThemeProperty, value);
    }

    // ── 事件 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 每次颜色提取完成后触发，传回本次实际使用的 IsDark 值。
    /// UseImageDominantTheme=true 时传回图片主色调判断结果，否则传回入参 IsDark。
    /// </summary>
    public event EventHandler<bool>? ThemeResolved;
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

    private PixelShaderEffect? _effect;
    private float _width;
    private float _height;
    private float _time;
    private float _rnd1 = 0f;
    private float _rnd2 = 0f;
    private float _rnd3 = 0f;
    private static readonly Random _random = new();

    private Vector3 _c1, _c2, _c3, _c4;
    private Vector3 _target1, _target2, _target3, _target4;
    private float _transitionProgress = 1f;
    private const float TransitionSpeed = 0.677f;

    private CancellationTokenSource? _loadCts;

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
                await fs.ReadExactlyAsync(shaderBytes);
            }

            _effect = new PixelShaderEffect(shaderBytes);
            _effect.Properties["EnableLightWave"] = true;
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
        _width = canvas.ConvertDipsToPixels((float)e.NewSize.Width, CanvasDpiRounding.Round);
        _height = canvas.ConvertDipsToPixels((float)e.NewSize.Height, CanvasDpiRounding.Round);
        _effect?.Properties["iResolution"] = new Vector2(_width, _height);
    }

    private void ApplyDefaultColors()
    {
        bool isDark = IsDark;
        _target1 = isDark ? new Vector3(0.08f, 0.08f, 0.08f) : new Vector3(0.95f, 0.95f, 0.95f);
        _target2 = isDark ? new Vector3(0.25f, 0.28f, 0.27f) : new Vector3(0.70f, 0.68f, 0.71f);
        _target3 = isDark ? new Vector3(0.07f, 0.07f, 0.07f) : new Vector3(0.80f, 0.80f, 0.80f);
        _target4 = isDark ? new Vector3(0.05f, 0.05f, 0.05f) : new Vector3(0.85f, 0.85f, 0.85f);
        _transitionProgress = 0f;
    }

    private void ApplyEffectProperties()
    {
        if (_effect == null) return;
        _width = canvas.ConvertDipsToPixels((float)canvas.Size.Width, CanvasDpiRounding.Round);
        _height = canvas.ConvertDipsToPixels((float)canvas.Size.Height, CanvasDpiRounding.Round);
        _effect.Properties["iResolution"] = new Vector2(_width, _height);
        _effect.Properties["iTime"] = _time;
        _effect.Properties["color1"] = _c1;
        _effect.Properties["color2"] = _c2;
        _effect.Properties["color3"] = _c3;
        _effect.Properties["color4"] = _c4;
        _effect.Properties["RandomValue1"] = _rnd1;
        _effect.Properties["RandomValue2"] = _rnd2;
        _effect.Properties["RandomValue3"] = _rnd3;
    }

    // ── 私有：图片加载与颜色提取 ────────────────────────────────────────

    private async Task LoadImageFromBytesAsync(byte[] imageBytes)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        bool isDark = IsDark;
        bool useImageDominantTheme = UseImageDominantTheme;   // 提前读取，避免后台线程访问 DP

        try
        {
            var (weighted, resolvedIsDark) = await Task.Run(async () =>   // ← 同时返回实际用的 isDark
            {
                using var memStream = new MemoryStream(imageBytes, writable: false);
                using var rasStream = memStream.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(rasStream);

                List<(Vector3 Color, int Population)> result;
                bool effectiveIsDark;
                using (var image = await ConvertToImageSharpAsync(decoder))
                {
                    cts.Token.ThrowIfCancellationRequested();

                    // 根据开关决定是否用图片主色调覆盖 IsDark
                    effectiveIsDark = useImageDominantTheme
                        ? (ColorThiefInstance.GetColor(image,10,false)?.IsDark ?? isDark)
                        : isDark;

                    var palette = ColorThiefInstance.GetPalette(image, 8, 10, false);
                    int totalPop = palette.Sum(t => t.Population);

                    var allByWeight = palette
                        .OrderByDescending(t => t.Population)
                        .ToList();

                    int preferredPop = allByWeight
                        .Where(t => t.IsDark == effectiveIsDark)
                        .Sum(t => t.Population);
                    float preferredRatio = totalPop > 0 ? (float)preferredPop / totalPop : 0f;

                    IEnumerable<QuantizedColor> candidates = preferredRatio >= 0.5f
                        ? allByWeight.Where(t => t.IsDark == effectiveIsDark)
                              .Concat(allByWeight.Where(t => t.IsDark != effectiveIsDark))
                        : allByWeight;

                    result = candidates
                        .Take(4)
                        .Select(t => (
                            Color: new Vector3(t.Color.R / 255f, t.Color.G / 255f, t.Color.B / 255f),
                            t.Population))
                        .ToList();
                }

                return (result, effectiveIsDark);

            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            if (weighted.Count == 0)
                weighted.Add((resolvedIsDark ? new Vector3(0.05f) : new Vector3(0.95f), 1));
            ScalePaletteLuminance(weighted, resolvedIsDark, useImageDominantTheme);

            var slots = DistributeByPopulation(weighted);

            _target1 = slots[0];
            _target2 = slots[1];
            _target3 = slots[2];
            _target4 = slots[3];
            _transitionProgress = 0f;
            _rnd1 = (float)(_random.NextDouble() * Math.PI * 2);
            _rnd2 = (float)(_random.NextDouble() * Math.PI * 2);
            _rnd3 = (float)(_random.NextDouble() * Math.PI * 2);
            // 回到 UI 线程触发事件，外部订阅者可安全操作 UI
            if (UseImageDominantTheme)
            {
                ThemeResolved?.Invoke(this, resolvedIsDark);
            }
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

        // 颜色 >= 3 种时，每种最多占 2 个 slot，保证至少 3 种颜色参与
        // 颜色 < 3 种时保持原有逻辑（单色最多 Total-1）
        int perColorMax = count >= 3 ? 2 : Total - 1;

        // 按比例分配 slot（向下取整 + 余数竞争），同时受 perColorMax 上限约束
        int[] slots = new int[count];
        int assigned = 0;
        for (int i = 0; i < count; i++)
        {
            slots[i] = Math.Min(perColorMax,
                (int)Math.Floor((float)weighted[i].Population / totalPop * Total));
            assigned += slots[i];
        }

        // 剩余 slot 逐个分给余数最大且未超上限的颜色
        int remaining = Total - assigned;
        for (int r = 0; r < remaining; r++)
        {
            int best = -1;
            float bestRem = -1f;
            for (int i = 0; i < count; i++)
            {
                if (slots[i] >= perColorMax) continue;
                float rem = (float)weighted[i].Population / totalPop * Total - slots[i];
                if (rem > bestRem) { bestRem = rem; best = i; }
            }
            // 所有颜色均已达上限（理论上不会发生）则放宽约束兜底
            if (best == -1)
            {
                for (int i = 0; i < count; i++)
                {
                    float rem = (float)weighted[i].Population / totalPop * Total - slots[i];
                    if (rem > bestRem) { bestRem = rem; best = i; }
                }
            }
            slots[best]++;
        }

        // 颜色 < 3 种时的兜底逻辑保持不变
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

        for (int i = Total - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
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
    private static void ScalePaletteLuminance(List<(Vector3 Color, int Population)> weighted, bool isDark, bool useImageDominantTheme)
    {
        // 以 Population 加权平均亮度为锚点：
        // 颜色间亮度差距完全不变（纯平移，无缩放）
        // 已满足则完全不动，最大程度贴近原图
        float targetAvg = isDark ? 0.45f : 0.55f;

        int count = weighted.Count;
        Span<float> hs = stackalloc float[count];
        Span<float> ss = stackalloc float[count];
        Span<float> ls = stackalloc float[count];

        int totalPop = 0;
        float weightedL = 0f;
        for (int i = 0; i < count; i++)
        {
            RgbToHsl(weighted[i].Color, out hs[i], out ss[i], out ls[i]);
            weightedL += ls[i] * weighted[i].Population;
            totalPop += weighted[i].Population;
        }

        float avgL = totalPop > 0 ? weightedL / totalPop : 0.5f;

        // 已满足，不动
        if (isDark && avgL <= targetAvg) return;
        if (!isDark && avgL >= targetAvg) return;

        float shift = targetAvg - avgL;
        float allTimeMin = 0f;
        float allTimeMax = 1f;
        // 平移后各颜色亮度上下限
        float clampMin = isDark ? allTimeMin : 0.3f;
        float clampMax = isDark ? 0.7f : allTimeMax;

        for (int i = 0; i < count; i++)
        {
            // 已处于目标侧边缘的颜色不做处理，保留原始亮度
            if (isDark && ls[i] < 0.3f) continue;
            if (!isDark && ls[i] > 0.7f) continue;
            float newL = Math.Clamp(ls[i] + shift, clampMin, clampMax);
            weighted[i] = (HslToRgb(hs[i], ss[i], newL), weighted[i].Population);
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
        Dispose(true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool dispose)
    {
        if (dispose)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;

            _effect?.Dispose();
            _effect = null;
            ThemeResolved = null;
        }
    }
}