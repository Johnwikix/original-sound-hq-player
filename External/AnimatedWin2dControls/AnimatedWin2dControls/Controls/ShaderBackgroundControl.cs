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
using System.Numerics;
using System.Runtime.CompilerServices;
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

        // EnableLightWave 变化时立即写入，不在热路径中，一次性装箱可接受
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

    // ── GC 优化：复用集合，避免换图时重复分配 ──────────────────────────────
    // LoadColorsFromBytesAsync 通过 CTS 保证同一时间只有一个 Task 在跑，静态复用安全
    private static readonly List<(Vector3 Color, int Population)> s_weightedBuffer = new(8);
    private static readonly List<QuantizedColor> s_paletteSortBuffer = new(8);

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

    // ── 热路径：每帧 Update ───────────────────────────────────────────────
    // 优化要点：
    //   1. iTime 每帧必须写入，装箱无法彻底消除，但限制为仅 1 次/帧。
    //   2. color1~4 / RandomValue 只在过渡期间写入；过渡完成后完全跳过，
    //      避免 _transitionProgress==1f 时每帧产生 4 个 Vector3 装箱对象。
    //   3. 平滑步函数内联，避免虚调用。

    private void OnCanvasUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs e)
    {
        if (_effect == null) return;

        _time = (float)e.Timing.TotalTime.TotalSeconds;
        _effect.Properties["iTime"] = _time;   // 1 次装箱/帧，不可避免

        if (_transitionProgress >= 1f) return;  // 过渡完成：提前退出，不再写颜色属性

        float delta = (float)e.Timing.ElapsedTime.TotalSeconds;
        _transitionProgress = Math.Min(1f, _transitionProgress + delta * TransitionSpeed);

        // smoothstep：内联避免委托开销
        float t = _transitionProgress * _transitionProgress * (3f - 2f * _transitionProgress);
        _c1 = Vector3.Lerp(_c1, _target1, t);
        _c2 = Vector3.Lerp(_c2, _target2, t);
        _c3 = Vector3.Lerp(_c3, _target3, t);
        _c4 = Vector3.Lerp(_c4, _target4, t);

        // 过渡期间每帧写入颜色（4 次装箱），结束后不再触发
        _effect.Properties["color1"] = _c1;
        _effect.Properties["color2"] = _c2;
        _effect.Properties["color3"] = _c3;
        _effect.Properties["color4"] = _c4;
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

        // 初始化时一次性写入所有属性，之后热路径只写变化项
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

    // 优化要点：
    //   1. 复用 s_weightedBuffer / s_paletteSortBuffer，避免 new List 分配。
    //   2. 用手动 foreach 替代 LINQ 链（Sum/Where/OrderByDescending/Concat/Take/Select），
    //      消除多个闭包对象和 IEnumerator 分配。
    //   3. palette.Sort() 在原列表上排序，不产生新集合。
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
            var (resolvedIsDark, weightedCount) = await Task.Run(async () =>
            {
                using var memStream = new MemoryStream(imageBytes, writable: false);
                using var rasStream = memStream.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(rasStream);

                bool effectiveIsDark;

                // 复用静态缓冲区，避免 new List 分配
                s_weightedBuffer.Clear();
                s_paletteSortBuffer.Clear();

                using (var image = await ConvertToImageSharpAsync(decoder))
                {
                    cts.Token.ThrowIfCancellationRequested();

                    effectiveIsDark = useImageDominantTheme
                        ? (ColorThiefInstance.GetColor(image, 10, false)?.IsDark ?? isDark)
                        : isDark;

                    var palette = ColorThiefInstance.GetPalette(image, 8, 10, false);

                    // ── 手动遍历统计，替代 LINQ Sum/Where ──
                    int totalPop = 0;
                    int preferredPop = 0;
                    foreach (var item in palette)
                    {
                        totalPop += item.Population;
                        if (item.IsDark == effectiveIsDark)
                            preferredPop += item.Population;

                        // 顺便复制到可排序缓冲区（palette 类型可能不支持直接 Sort）
                        s_paletteSortBuffer.Add(item);
                    }

                    bool usePreferred = totalPop > 0
                        && (float)preferredPop / totalPop >= 0.5f;

                    // 按 Population 降序排序（原地，不产生新集合）
                    s_paletteSortBuffer.Sort(static (a, b) =>
                        b.Population.CompareTo(a.Population));

                    // ── 两遍填充：先取偏好色，再补足 ──
                    // 最多取 4 个，总量很小，两遍线性扫描比 LINQ 链更高效且无分配
                    int taken = 0;

                    if (usePreferred)
                    {
                        foreach (var item in s_paletteSortBuffer)
                        {
                            if (taken == 4) break;
                            if (item.IsDark == effectiveIsDark)
                            {
                                s_weightedBuffer.Add((
                                    new Vector3(item.Color.R / 255f,
                                                item.Color.G / 255f,
                                                item.Color.B / 255f),
                                    item.Population));
                                taken++;
                            }
                        }
                    }

                    foreach (var item in s_paletteSortBuffer)
                    {
                        if (taken == 4) break;
                        // usePreferred 时跳过已经加过的颜色
                        if (usePreferred && item.IsDark == effectiveIsDark) continue;
                        s_weightedBuffer.Add((
                            new Vector3(item.Color.R / 255f,
                                        item.Color.G / 255f,
                                        item.Color.B / 255f),
                            item.Population));
                        taken++;
                    }
                }

                return (effectiveIsDark, s_weightedBuffer.Count);
            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            // s_weightedBuffer 此时持有结果，在 UI 线程侧读取（Task 已结束，安全）
            if (weightedCount == 0)
                s_weightedBuffer.Add((resolvedIsDark
                    ? new Vector3(0.05f)
                    : new Vector3(0.95f), 1));

            ScalePaletteLuminance(s_weightedBuffer, resolvedIsDark, useImageDominantTheme);

            var slots = DistributeByPopulation(s_weightedBuffer);
            _target1 = slots[0];
            _target2 = slots[1];
            _target3 = slots[2];
            _target4 = slots[3];
            _transitionProgress = 0f;

            _rnd1 = (float)(_random.NextDouble() * Math.PI * 2);
            _rnd2 = (float)(_random.NextDouble() * Math.PI * 2);
            _rnd3 = (float)(_random.NextDouble() * Math.PI * 2);

            // 随机值也只在颜色更新时写入一次，不在热路径
            if (_effect != null)
            {
                _effect.Properties["RandomValue1"] = _rnd1;
                _effect.Properties["RandomValue2"] = _rnd2;
                _effect.Properties["RandomValue3"] = _rnd3;
            }

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

    // 优化要点：
    //   1. slots 用 stackalloc 替代 new int[]，消除堆分配。
    //   2. totalPop 手动循环计算，替代 LINQ Sum（有 IEnumerator 分配）。
    //   3. result 固定长度 4，仍走堆分配，但每次换图只调用一次，可接受；
    //      若需进一步优化可改为 stackalloc + out 参数。
    private static Vector3[] DistributeByPopulation(
        List<(Vector3 Color, int Population)> weighted)
    {
        const int Total = 4;
        int count = weighted.Count;

        // 手动求和，替代 LINQ Sum（避免 IEnumerator 分配）
        int totalPop = 0;
        for (int i = 0; i < count; i++)
            totalPop += weighted[i].Population;

        int perColorMax = count >= 3 ? 2 : Total - 1;

        // stackalloc：count 最大为 4（Take(4) 限制），安全
        Span<int> slots = stackalloc int[count <= 8 ? count : 8];
        slots = slots[..count];
        slots.Clear();

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
            int best = -1;
            float bestRem = -1f;
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
            count = 2;
            // 重新计算 slots（退化路径，极少触发）
            slots = stackalloc int[2];
            slots[0] = Total - 1;
            slots[1] = 1;
        }

        var result = new Vector3[Total];
        int idx = 0;
        for (int i = 0; i < count && idx < Total; i++)
            for (int j = 0; j < slots[i] && idx < Total; j++)
                result[idx++] = weighted[i].Color;

        // Fisher-Yates shuffle
        for (int i = Total - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }

    // 优化要点：Span<float> 替代 float[]（堆分配），其余逻辑不变。
    private static void ScalePaletteLuminance(
        List<(Vector3 Color, int Population)> weighted,
        bool isDark, bool useImageDominantTheme)
    {
        float targetAvg = isDark ? 0.45f : 0.55f;
        int count = weighted.Count;

        // stackalloc 替代隐式 float[]，count 最大 5（含 nudge 补充的 1 个）
        Span<float> hs = stackalloc float[count <= 8 ? count : 8];
        Span<float> ss = stackalloc float[count <= 8 ? count : 8];
        Span<float> ls = stackalloc float[count <= 8 ? count : 8];
        hs = hs[..count];
        ss = ss[..count];
        ls = ls[..count];

        int totalPop = 0;
        float weightedL = 0f;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        // 随机值随 shuffle 一起写入，不在热路径
        if (_effect != null)
        {
            _effect.Properties["RandomValue1"] = _rnd1;
            _effect.Properties["RandomValue2"] = _rnd2;
            _effect.Properties["RandomValue3"] = _rnd3;
        }
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