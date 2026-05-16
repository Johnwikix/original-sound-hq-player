using AnimatedWin2dControls.Utils;
using ColorThief.ImageSharp.Shared;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
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

namespace AnimatedWin2dControls.Controls;

/// <summary>
/// 渐变流体背景，Powered by Isolation https://github.com/Storyteller-Studios/Isolation
/// </summary>
[TemplatePart(Name = PartCanvasName, Type = typeof(CanvasAnimatedControl))]
public sealed class ShaderBackgroundControl : Control, IDisposable
{
    // ── 模板部件名 ────────────────────────────────────────────────────────

    private const string PartCanvasName = "PART_Canvas";

    // ── 依赖属性 ──────────────────────────────────────────────────────────

    public static readonly DependencyProperty IsBackgroundEnableProperty =
        DependencyProperty.Register(nameof(IsBackgroundEnable), typeof(bool),
            typeof(ShaderBackgroundControl), new PropertyMetadata(true, OnIsBackgroundEnableChanged));

    public bool IsBackgroundEnable
    {
        get => (bool)GetValue(IsBackgroundEnableProperty);
        set => SetValue(IsBackgroundEnableProperty, value);
    }

    private static void OnIsBackgroundEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ShaderBackgroundControl)d)._isBackgroundEnable = (bool)e.NewValue;

    public static readonly DependencyProperty EnableLightWaveProperty =
        DependencyProperty.Register(nameof(EnableLightWave), typeof(bool),
            typeof(ShaderBackgroundControl), new PropertyMetadata(false, OnColorParamChanged));

    public bool EnableLightWave
    {
        get => (bool)GetValue(EnableLightWaveProperty);
        set => SetValue(EnableLightWaveProperty, value);
    }

    public static readonly DependencyProperty UseImageDominantThemeProperty =
        DependencyProperty.Register(nameof(UseImageDominantTheme), typeof(bool),
            typeof(ShaderBackgroundControl), new PropertyMetadata(false, OnColorParamChanged));

    public bool UseImageDominantTheme
    {
        get => (bool)GetValue(UseImageDominantThemeProperty);
        set => SetValue(UseImageDominantThemeProperty, value);
    }

    public static readonly DependencyProperty ImageBytesProperty =
        DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]),
            typeof(ShaderBackgroundControl), new PropertyMetadata(null, OnImageBytesChanged));

    public byte[] ImageBytes
    {
        get => (byte[])GetValue(ImageBytesProperty);
        set => SetValue(ImageBytesProperty, value);
    }

    public static readonly DependencyProperty IsDarkProperty =
        DependencyProperty.Register(nameof(IsDark), typeof(bool),
            typeof(ShaderBackgroundControl), new PropertyMetadata(true, OnColorParamChanged));

    public bool IsDark
    {
        get => (bool)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    // ── 事件 ──────────────────────────────────────────────────────────────

    /// <summary>当 UseImageDominantTheme=true 时，图片亮/暗主题解析完成后触发。</summary>
    public event EventHandler<bool>? ThemeResolved;

    // ── 依赖属性回调 ──────────────────────────────────────────────────────

    private static void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (ShaderBackgroundControl)d;
        if (ctrl._effect == null) { ctrl.ApplyDefaultColors(); return; }

        if (e.NewValue is byte[] bytes && bytes.Length > 0)
        {
            if (ctrl.IsDuplicateAndUpdate(bytes))
            {
                ctrl.ShuffleCurrentColors();
                return;
            }
            _ = ctrl.LoadColorsFromBytesAsync(bytes);
        }
        else
        {
            ctrl.IsDuplicateAndUpdate(null);
            ctrl.ApplyDefaultColors();
        }
    }

    private static void OnColorParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (ShaderBackgroundControl)d;
        if (ctrl._effect == null) return;
        ctrl._effect.Properties["EnableLightWave"] = ctrl.EnableLightWave;
        if (ctrl.ImageBytes is byte[] bytes && bytes.Length > 0)
            _ = ctrl.LoadColorsFromBytesAsync(bytes);
        else
            ctrl.ApplyDefaultColors();
    }

    // ── 私有字段 ──────────────────────────────────────────────────────────

    private CanvasAnimatedControl? _canvas;
    private PixelShaderEffect? _effect;
    private float _width;
    private float _height;
    private float _time;
    private float _rnd1, _rnd2, _rnd3;
    private static readonly Random _random = new();
    private bool _isBackgroundEnable = true;

    private Vector3 _c1, _c2, _c3, _c4;
    private Vector3 _target1, _target2, _target3, _target4;
    private float _transitionProgress = 1f;
    private const float TransitionSpeed = 0.5f;

    private CancellationTokenSource? _loadCts;
    private static readonly ColorThief.ImageSharp.ColorThief ColorThiefInstance = new();

    private long _lastLength = -1;
    private int _lastHash;

    // ── 构造函数 ──────────────────────────────────────────────────────────

    public ShaderBackgroundControl()
    {
        DefaultStyleKey = typeof(ShaderBackgroundControl);
        Unloaded += (_, _) => Dispose(true);
    }

    // ── 模板应用 ──────────────────────────────────────────────────────────

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // 卸载旧 canvas 的事件（模板可能被重新应用）
        DetachCanvasEvents();

        _canvas = GetTemplateChild(PartCanvasName) as CanvasAnimatedControl;

        if (_canvas is not null)
        {
            AttachCanvasEvents(_canvas);
        }
    }

    private void AttachCanvasEvents(CanvasAnimatedControl canvas)
    {
        canvas.CreateResources += OnCanvasCreateResources;
        canvas.Update += OnCanvasUpdate;
        canvas.Draw += OnCanvasDraw;
        canvas.SizeChanged += OnCanvasSizeChanged;
    }

    private void DetachCanvasEvents()
    {
        if (_canvas is null) return;
        _canvas.CreateResources -= OnCanvasCreateResources;
        _canvas.Update -= OnCanvasUpdate;
        _canvas.Draw -= OnCanvasDraw;
        _canvas.SizeChanged -= OnCanvasSizeChanged;
    }

    // ── Canvas 事件处理 ───────────────────────────────────────────────────

    private async void OnCanvasCreateResources(
        CanvasAnimatedControl sender,
        Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        // args.TrackAsyncAction 让框架等待异步资源加载完成
        args.TrackAsyncAction(LoadResourcesAsync(sender).AsAsyncAction());
    }

    private async Task LoadResourcesAsync(CanvasAnimatedControl sender)
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "Shaders", "effect.bin");
        byte[] shaderBytes;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                       FileShare.Read, 4096, useAsync: true))
        {
            shaderBytes = new byte[fs.Length];
            await fs.ReadExactlyAsync(shaderBytes);
        }

        _effect = new PixelShaderEffect(shaderBytes);

        if (ImageBytes is { Length: > 0 } bytes)
            await LoadColorsFromBytesAsync(bytes);
        else
            ApplyDefaultColors();

        _c1 = _target1; _c2 = _target2;
        _c3 = _target3; _c4 = _target4;
        _transitionProgress = 1f;
        ApplyEffectProperties(sender);
    }

    private void OnCanvasUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs e)
    {
        if (_effect == null) return;

        _time = (float)e.Timing.TotalTime.TotalSeconds;
        _effect.Properties["iTime"] = _time;

        float delta = (float)e.Timing.ElapsedTime.TotalSeconds;

        if (_transitionProgress < 1f)
        {
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
        }
    }

    private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs e)
    {
        e.DrawingSession.Clear(Microsoft.UI.Colors.Transparent);
        if (_effect == null) return;

        if (_isBackgroundEnable)
            e.DrawingSession.DrawImage(_effect);
        else
            e.DrawingSession.FillRectangle(0, 0, 1, 1, Microsoft.UI.Colors.Transparent);
    }

    // ── 尺寸 ──────────────────────────────────────────────────────────────

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_canvas is null || _effect is null) return;
        _width = _canvas.ConvertDipsToPixels((float)e.NewSize.Width, CanvasDpiRounding.Round);
        _height = _canvas.ConvertDipsToPixels((float)e.NewSize.Height, CanvasDpiRounding.Round);
        _effect.Properties["iResolution"] = new Vector2(_width, _height);
    }

    // ── 颜色 ──────────────────────────────────────────────────────────────

    private void ApplyDefaultColors()
    {
        bool isDark = IsDark;
        _target1 = isDark ? new Vector3(0.08f, 0.08f, 0.08f) : new Vector3(0.95f, 0.95f, 0.95f);
        _target2 = isDark ? new Vector3(0.25f, 0.28f, 0.27f) : new Vector3(0.70f, 0.68f, 0.71f);
        _target3 = isDark ? new Vector3(0.07f, 0.07f, 0.07f) : new Vector3(0.80f, 0.80f, 0.80f);
        _target4 = isDark ? new Vector3(0.05f, 0.05f, 0.05f) : new Vector3(0.85f, 0.85f, 0.85f);
        _transitionProgress = 0f;
    }

    private void ApplyEffectProperties(CanvasAnimatedControl canvas)
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
        _effect.Properties["EnableLightWave"] = EnableLightWave;
        _effect.Properties["RandomValue1"] = _rnd1;
        _effect.Properties["RandomValue2"] = _rnd2;
        _effect.Properties["RandomValue3"] = _rnd3;
    }

    // ── 颜色提取 ──────────────────────────────────────────────────────────

    public async Task LoadImageAsync(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0) return;
        await LoadColorsFromBytesAsync(imageBytes);
    }

    private async Task LoadColorsFromBytesAsync(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0) return;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        bool isDark = IsDark;
        bool useImageDominantTheme = UseImageDominantTheme;

        try
        {
            var (weighted, resolvedIsDark) = await Task.Run(async () =>
            {
                using var memStream = new MemoryStream(imageBytes, writable: false);
                using var rasStream = memStream.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(rasStream);

                bool effectiveIsDark;
                List<(Vector3 Color, int Population)> result;

                using (var image = await ConvertToImageSharpAsync(decoder))
                {
                    cts.Token.ThrowIfCancellationRequested();

                    effectiveIsDark = useImageDominantTheme
                        ? (ColorThiefInstance.GetColor(image, 10, false)?.IsDark ?? isDark)
                        : isDark;

                    var palette = ColorThiefInstance.GetPalette(image, 8, 10, false);
                    int totalPop = palette.Sum(t => t.Population);

                    var allByWeight = palette.OrderByDescending(t => t.Population).ToList();

                    int preferredPop = allByWeight
                        .Where(t => t.IsDark == effectiveIsDark)
                        .Sum(t => t.Population);
                    float preferredRatio = totalPop > 0 ? (float)preferredPop / totalPop : 0f;

                    IEnumerable<QuantizedColor> candidates = preferredRatio >= 0.5f
                        ? allByWeight.Where(t => t.IsDark == effectiveIsDark)
                              .Concat(allByWeight.Where(t => t.IsDark != effectiveIsDark))
                        : allByWeight;

                    result = candidates.Take(4)
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

            if (UseImageDominantTheme)
                ThemeResolved?.Invoke(this, resolvedIsDark);
        }
        catch (OperationCanceledException) { }
        catch
        {
            ApplyDefaultColors();
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

    // ── 颜色算法 ──────────────────────────────────────────────────────────

    private static Vector3[] DistributeByPopulation(List<(Vector3 Color, int Population)> weighted)
    {
        const int Total = 4;
        int count = weighted.Count;
        int totalPop = weighted.Sum(w => w.Population);
        int perColorMax = count >= 3 ? 2 : Total - 1;

        int[] slots = new int[count];
        int assigned = 0;
        for (int i = 0; i < count; i++)
        {
            slots[i] = Math.Min(perColorMax,
                (int)Math.Floor((float)weighted[i].Population / totalPop * Total));
            assigned += slots[i];
        }

        int remaining = Total - assigned;
        for (int r = 0; r < remaining; r++)
        {
            int best = -1; float bestRem = -1f;
            for (int i = 0; i < count; i++)
            {
                if (slots[i] >= perColorMax) continue;
                float rem = (float)weighted[i].Population / totalPop * Total - slots[i];
                if (rem > bestRem) { bestRem = rem; best = i; }
            }
            if (best == -1)
                for (int i = 0; i < count; i++)
                {
                    float rem = (float)weighted[i].Population / totalPop * Total - slots[i];
                    if (rem > bestRem) { bestRem = rem; best = i; }
                }
            slots[best]++;
        }

        if (count >= 2 && slots[0] == Total) { slots[0] = Total - 1; slots[1] = 1; }
        else if (count == 1)
        {
            var nudge = Vector3.Clamp(
                weighted[0].Color + new Vector3(0.05f, -0.03f, 0.04f),
                new Vector3(0.01f), new Vector3(0.99f));
            weighted.Add((nudge, 0));
            slots = new[] { Total - 1, 1 };
            count = 2;
        }

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

    private static void ScalePaletteLuminance(
        List<(Vector3 Color, int Population)> weighted,
        bool isDark, bool useImageDominantTheme)
    {
        float targetAvg = isDark ? 0.45f : 0.55f;
        int count = weighted.Count;
        Span<float> hs = stackalloc float[count];
        Span<float> ss = stackalloc float[count];
        Span<float> ls = stackalloc float[count];

        int totalPop = 0; float weightedL = 0f;
        for (int i = 0; i < count; i++)
        {
            RgbToHsl(weighted[i].Color, out hs[i], out ss[i], out ls[i]);
            weightedL += ls[i] * weighted[i].Population;
            totalPop += weighted[i].Population;
        }

        float avgL = totalPop > 0 ? weightedL / totalPop : 0.5f;
        if (isDark && avgL <= targetAvg) return;
        if (!isDark && avgL >= targetAvg) return;

        float shift = targetAvg - avgL;
        float clampMin = isDark ? 0f : 0.3f;
        float clampMax = isDark ? 0.7f : 1f;

        for (int i = 0; i < count; i++)
        {
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
        return new Vector3(HueToRgb(p, q, h + 1f / 3f),
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
        float scale = Math.Min(1f, Math.Min((float)MaxSize / srcWidth,
                                               (float)MaxSize / srcHeight));
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

    // ── 工具方法 ──────────────────────────────────────────────────────────

    public bool IsDuplicateAndUpdate(byte[]? newBytes)
    {
        if (newBytes is not { Length: > 0 })
        {
            bool wasEmpty = _lastLength == 0;
            _lastLength = 0;
            _lastHash = 0;
            return wasEmpty;
        }

        int hash = ToolUtils.ComputeFastHash(newBytes);
        if (newBytes.Length == _lastLength && hash == _lastHash)
            return true;

        _lastLength = newBytes.Length;
        _lastHash = hash;
        return false;
    }

    private void ShuffleCurrentColors()
    {
        var slots = new[] { _target1, _target2, _target3, _target4 };
        for (int i = slots.Length - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (slots[i], slots[j]) = (slots[j], slots[i]);
        }
        _target1 = slots[0]; _target2 = slots[1];
        _target3 = slots[2]; _target4 = slots[3];
        _rnd1 = (float)(_random.NextDouble() * Math.PI * 2);
        _rnd2 = (float)(_random.NextDouble() * Math.PI * 2);
        _rnd3 = (float)(_random.NextDouble() * Math.PI * 2);
        _transitionProgress = 0f;
    }

    // ── 释放 ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool dispose)
    {
        if (!dispose) return;
        DetachCanvasEvents();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _effect?.Dispose();
        _effect = null;
        ThemeResolved = null;
    }
}