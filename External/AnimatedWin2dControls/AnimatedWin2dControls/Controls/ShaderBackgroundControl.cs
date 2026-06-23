using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

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

    /// <summary>
    /// 控件内部发生非预期异常时触发，可用于外部日志/诊断。
    /// 注意：热路径（OnCanvasUpdate / OnCanvasDraw）不捕获异常，不会触发此事件。
    /// </summary>
    public event EventHandler<Exception>? ExceptionOccurred;

    // ── 依赖属性回调 ──────────────────────────────────────────────────────

    private static void OnColorParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (ShaderBackgroundControl)d;
        try
        {
            if (ctrl._effect == null) return;
            ctrl._effect.Properties["EnableLightWave"] = ctrl.EnableLightWave;
            if (ctrl._currentPalette is not null)
                ctrl.ApplyPaletteColors(ctrl._currentPalette);
            else
                ctrl.ApplyDefaultColors();
        }
        catch (Exception ex) { ctrl.RaiseException(ex); }
    }

    // ── 私有字段 ──────────────────────────────────────────────────────────

    private CanvasAnimatedControl? _canvas;
    private PixelShaderEffect? _effect;
    private float _width;
    private float _height;
    private float _time;
    private float _rnd1, _rnd2, _rnd3;
    private static readonly Random _random = new();
    private bool _pausedByVisibility;
    private bool _pausedByParent;
    private bool _pausedByWindow;
    private long _visibilityCallbackToken;

    private Vector3 _c1, _c2, _c3, _c4;
    private Vector3 _target1, _target2, _target3, _target4;
    private float _transitionProgress = 1f;
    private const float TransitionSpeed = 0.5f;

    private Impressionist.PaletteResult? _currentPalette;

    // ── 构造函数 ──────────────────────────────────────────────────────────

    public ShaderBackgroundControl()
    {
        DefaultStyleKey = typeof(ShaderBackgroundControl);
        Unloaded += (_, _) => Dispose(true);
    }

    // ── 模板应用 ──────────────────────────────────────────────────────────

    protected override void OnApplyTemplate()
    {
        try
        {
            base.OnApplyTemplate();
            DetachCanvasEvents();
            _canvas = GetTemplateChild(PartCanvasName) as CanvasAnimatedControl;
            if (_canvas is not null)
            {
                AttachCanvasEvents(_canvas);
                UpdateCanvasPaused();
            }
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        }
        catch (Exception ex) { RaiseException(ex); }
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

    public void PauseRendering()
    {
        _pausedByParent = true;
        UpdateCanvasPaused();
    }

    public void ResumeRendering()
    {
        _pausedByParent = false;
        UpdateCanvasPaused();
    }

    public void SetWindowPaused(bool paused)
    {
        _pausedByWindow = paused;
        UpdateCanvasPaused();
    }

    private void UpdateCanvasPaused()
    {
        if (_canvas is not null)
            _canvas.Paused = _pausedByVisibility || _pausedByParent || _pausedByWindow;
    }

    private static void OnVisibilityChanged(DependencyObject d, DependencyProperty dp)
    {
        var ctrl = (ShaderBackgroundControl)d;
        ctrl._pausedByVisibility = ctrl.Visibility != Visibility.Visible;
        ctrl.UpdateCanvasPaused();
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
        try
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

            sender.IsFixedTimeStep = true;
            sender.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 40.0);

            if (_currentPalette is not null)
                ApplyPaletteColors(_currentPalette);
            else
                ApplyDefaultColors();

            _c1 = _target1; _c2 = _target2;
            _c3 = _target3; _c4 = _target4;
            _transitionProgress = 1f;
            ApplyEffectProperties(sender);
        }
        catch (Exception ex) { RaiseException(ex); }
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
        if (_effect != null)
            e.DrawingSession.DrawImage(_effect);
    }

    // ── 尺寸 ──────────────────────────────────────────────────────────────

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            if (_canvas is null || _effect is null) return;
            _width = _canvas.ConvertDipsToPixels((float)e.NewSize.Width, CanvasDpiRounding.Round);
            _height = _canvas.ConvertDipsToPixels((float)e.NewSize.Height, CanvasDpiRounding.Round);
            _effect.Properties["iResolution"] = new Vector2(_width, _height);
        }
        catch (Exception ex) { RaiseException(ex); }
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

    public void SetPalette(Impressionist.PaletteResult? palette)
    {
        _currentPalette = palette;
        if (palette?.Palette is not { Count: > 0 }) { ApplyDefaultColors(); return; }
        if (_effect == null) return;
        ApplyPaletteColors(palette);
    }

    private void ApplyPaletteColors(Impressionist.PaletteResult palette)
    {
        bool isDark = UseImageDominantTheme ? palette.PaletteIsDark : IsDark;
        var weighted = new List<(Vector3 Color, int Population)>(palette.Palette.Count);
        for (int i = 0; i < palette.Palette.Count; i++)
            weighted.Add((palette.Palette[i] / 255f, Math.Max(1, palette.Palette.Count - i)));

        ScalePaletteLuminance(weighted, isDark, UseImageDominantTheme);
        var slots = DistributeByPopulation(weighted);
        _target1 = slots[0];
        _target2 = slots[1];
        _target3 = slots[2];
        _target4 = slots[3];
        _transitionProgress = 0f;

        _rnd1 = (float)(_random.NextDouble() * Math.PI * 2);
        _rnd2 = (float)(_random.NextDouble() * Math.PI * 2);
        _rnd3 = (float)(_random.NextDouble() * Math.PI * 2);
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

    private static void ScalePaletteLuminance(
        List<(Vector3 Color, int Population)> weighted,
        bool isDark, bool useImageDominantTheme)
    {
        float targetAvg = isDark ? 0.45f : 0.55f;
        int count = weighted.Count;

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

    // ── 释放 ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool dispose)
    {
        if (!dispose) return;
        if (_canvas is not null)
            _canvas.Paused = true;
        UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
        DetachCanvasEvents();
        _effect?.Dispose();
        _effect = null;
        ThemeResolved = null;
        ExceptionOccurred = null;
    }

    // ── 异常上报 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 在 UI 线程上安全触发 <see cref="ExceptionOccurred"/>。
    /// 若订阅者自身抛出异常，不再递归上报，直接吞掉以防无限循环。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RaiseException(Exception ex)
    {
        try
        {
            ExceptionOccurred?.Invoke(this, ex);
        }
        catch { /* 订阅者异常不再递归上报 */ }
    }
}