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
    // ── 依赖属性 ────────────────────────────────────────────────────────

    public static readonly DependencyProperty EnableLightWaveProperty =
        DependencyProperty.Register(nameof(EnableLightWave), typeof(bool),
            typeof(GradientBackgroundControl), new PropertyMetadata(false, OnColorParamChanged));

    public bool EnableLightWave
    {
        get => (bool)GetValue(EnableLightWaveProperty);
        set => SetValue(EnableLightWaveProperty, value);
    }

    public static readonly DependencyProperty UseImageDominantThemeProperty =
        DependencyProperty.Register(nameof(UseImageDominantTheme), typeof(bool),
            typeof(GradientBackgroundControl), new PropertyMetadata(false, OnColorParamChanged));

    public bool UseImageDominantTheme
    {
        get => (bool)GetValue(UseImageDominantThemeProperty);
        set => SetValue(UseImageDominantThemeProperty, value);
    }

    public static readonly DependencyProperty ImageBytesProperty =
        DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]),
            typeof(GradientBackgroundControl), new PropertyMetadata(null, OnImageBytesChanged));

    public byte[] ImageBytes
    {
        get => (byte[])GetValue(ImageBytesProperty);
        set => SetValue(ImageBytesProperty, value);
    }

    public static readonly DependencyProperty IsDarkProperty =
        DependencyProperty.Register(nameof(IsDark), typeof(bool),
            typeof(GradientBackgroundControl), new PropertyMetadata(true, OnColorParamChanged));

    public bool IsDark
    {
        get => (bool)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    // ── 事件 ────────────────────────────────────────────────────────────

    public event EventHandler<bool>? ThemeResolved;

    // ── 依赖属性回调 ─────────────────────────────────────────────────────

    private static void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (GradientBackgroundControl)d;
        if (ctrl._effect == null) return;

        if (e.NewValue is byte[] bytes && bytes.Length > 0)
            _ = ctrl.LoadImageFromBytesAsync(bytes);
        else
            ctrl.ApplyDefaultColors();
    }

    private static void OnColorParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (GradientBackgroundControl)d;
        if (ctrl._effect == null) return;
        ctrl._effect.Properties["EnableLightWave"] = ctrl.EnableLightWave;
        if (ctrl.ImageBytes is byte[] bytes && bytes.Length > 0)
            _ = ctrl.LoadImageFromBytesAsync(bytes);
        else
            ctrl.ApplyDefaultColors();
    }

    // ── 私有字段：Shader / 动画 ──────────────────────────────────────────

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
    private static readonly ColorThief.ImageSharp.ColorThief ColorThiefInstance = new();

    // ── 私有字段：图片显示 ───────────────────────────────────────────────

    // 图片边距（上下绝对像素，左右相对正方形边长比例）
    private double _imgMarginTop = 0.05;
    private double _imgMarginLeft = 0.2;
    private double _imgMarginRight = 0.2;
    private double _imgMarginBottom = 0.4;
    private double _radius = 16.0;

    // 当前显示的位图（稳定显示或正在淡出）
    private CanvasBitmap? _currentBitmap;
    private float _imgAlpha = 0f;

    // 下一张位图（正在淡入）
    private CanvasBitmap? _nextBitmap;
    private float _imgNextAlpha = 0f;

    // 交叉淡入淡出状态
    private bool _isCrossFading = false;
    private const float ImageFadeSpeed = 2.0f;

    // 已解码、等待 Update 接管的位图
    private CanvasBitmap? _pendingBitmap;
    private bool _hasPendingBitmap = false;

    // 设备就绪前到达的像素数据
    private byte[]? _pendingPixels;
    private int _pendingBmpW;
    private int _pendingBmpH;

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

    // ── Canvas 事件注册 ──────────────────────────────────────────────────

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
            shaderBytes = null;

            // 若设备就绪前已有像素数据等待，现在补建位图
            if (_pendingPixels != null)
            {
                var bitmap = CanvasBitmap.CreateFromBytes(
                    canvas,
                    _pendingPixels,
                    _pendingBmpW,
                    _pendingBmpH,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
                _pendingBitmap?.Dispose();
                _pendingBitmap = bitmap;
                _hasPendingBitmap = true;
                _pendingPixels = null;
            }

            if (ImageBytes is { Length: > 0 } bytes)
                await LoadImageFromBytesAsync(bytes);
            else
                ApplyDefaultColors();

            // 首次直接应用目标色，无需过渡
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

            float delta = (float)e.Timing.ElapsedTime.TotalSeconds;

            // ── 颜色过渡 ─────────────────────────────────────────────
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

            // ── 图片淡入淡出 ─────────────────────────────────────────
            UpdateImageFade(delta);
        };

        canvas.Draw += (s, e) =>
        {
            if (_effect == null) return;
            e.DrawingSession.DrawImage(_effect);
            DrawImageLayer(e.DrawingSession);
        };
    }

    // ── 尺寸变化 ─────────────────────────────────────────────────────────

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _width = canvas.ConvertDipsToPixels((float)e.NewSize.Width, CanvasDpiRounding.Round);
        _height = canvas.ConvertDipsToPixels((float)e.NewSize.Height, CanvasDpiRounding.Round);
        _effect?.Properties["iResolution"] = new Vector2(_width, _height);
    }

    // ── 默认颜色 ─────────────────────────────────────────────────────────

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
        _effect.Properties["EnableLightWave"] = EnableLightWave;
        _effect.Properties["RandomValue1"] = _rnd1;
        _effect.Properties["RandomValue2"] = _rnd2;
        _effect.Properties["RandomValue3"] = _rnd3;
    }

    // ── 图片淡入淡出逻辑 ─────────────────────────────────────────────────

    private void UpdateImageFade(float delta)
    {
        if (_hasPendingBitmap)
        {
            _hasPendingBitmap = false;

            // 快速切换：将正在淡入的 next 升级为 current，保留当前透明度
            if (_isCrossFading && _nextBitmap != null)
            {
                _currentBitmap?.Dispose();
                _currentBitmap = _nextBitmap;
                _imgAlpha = _imgNextAlpha;
                _nextBitmap = null;
                _imgNextAlpha = 0f;
            }

            _nextBitmap?.Dispose();
            _nextBitmap = _pendingBitmap;
            _pendingBitmap = null;
            _imgNextAlpha = 0f;
            _isCrossFading = true;
        }

        if (!_isCrossFading) return;

        // 当前图淡出
        if (_currentBitmap != null)
            _imgAlpha = Math.Max(0f, _imgAlpha - delta * ImageFadeSpeed);

        // 下一张淡入
        if (_nextBitmap != null)
            _imgNextAlpha = Math.Min(1f, _imgNextAlpha + delta * ImageFadeSpeed);

        // 淡入完成：next 升级为 current
        if (_imgNextAlpha >= 1f)
        {
            _currentBitmap?.Dispose();
            _currentBitmap = _nextBitmap;
            _imgAlpha = 1f;
            _nextBitmap = null;
            _imgNextAlpha = 0f;
            _isCrossFading = false;
        }
    }

    // ── 图片绘制 ─────────────────────────────────────────────────────────

    private void DrawImageLayer(CanvasDrawingSession ds)
    {
        if (_width <= 0 || _height <= 0) return;

        float canvasW = (float)canvas.Size.Width;
        float canvasH = (float)canvas.Size.Height;

        // 左半区域
        float regionW = canvasW * 0.5f;
        float regionH = canvasH;

        // 最大正方形（居中）
        float squareSize = Math.Min(regionW, regionH);
        float squareX = (regionW - squareSize) * 0.5f;
        float squareY = (regionH - squareSize) * 0.5f;

        // margin（全部基于 squareSize ✔）
        float padTop = (float)(_imgMarginTop * squareSize);
        float padBottom = (float)(_imgMarginBottom * squareSize);
        float padLeft = (float)(_imgMarginLeft * squareSize);
        float padRight = (float)(_imgMarginRight * squareSize);

        // contentRect（关键：真正的布局区域）
        float contentX = squareX + padLeft;
        float contentY = squareY + padTop;
        float contentW = squareSize - padLeft - padRight;
        float contentH = squareSize - padTop - padBottom;

        if (contentW <= 0 || contentH <= 0) return;

        CanvasBitmap? refBitmap = _nextBitmap ?? _currentBitmap;
        if (refBitmap == null) return;

        float imgW = (float)refBitmap.SizeInPixels.Width;
        float imgH = (float)refBitmap.SizeInPixels.Height;
        if (imgW <= 0 || imgH <= 0) return;

        float imgAspect = imgW / imgH;

        float drawW, drawH;

        // aspect-fit（保持你当前行为）
        if (imgAspect >= contentW / contentH)
        {
            drawW = contentW;
            drawH = drawW / imgAspect;
        }
        else
        {
            drawH = contentH;
            drawW = drawH * imgAspect;
        }

        // ⭐ 在 contentRect 内居中（关键修正点）
        float drawX = contentX + (contentW - drawW) * 0.5f;
        float drawY = contentY + (contentH - drawH) * 0.5f;

        var destRect = new Windows.Foundation.Rect(drawX, drawY, drawW, drawH);
        float radius = (float)_radius;

        if (_currentBitmap != null && _imgAlpha > 0f)
            DrawRoundedImageWithShadow(ds, _currentBitmap, destRect, radius, _imgAlpha);

        if (_nextBitmap != null && _imgNextAlpha > 0f)
            DrawRoundedImageWithShadow(ds, _nextBitmap, destRect, radius, _imgNextAlpha);
    }

    private void DrawRoundedImageWithShadow(
        CanvasDrawingSession ds,
        CanvasBitmap bitmap,
        Windows.Foundation.Rect destRect,
        float radius,
        float opacity)
    {
        float w = (float)destRect.Width;
        float h = (float)destRect.Height;
        if (w <= 0 || h <= 0) return;

        // 从 DrawingSession 获取设备，保证所有效果资源在同一设备上
        var device = ds.Device;

        var scaleEffect = new ScaleEffect
        {
            Source = bitmap,
            Scale = new Vector2(w / (float)bitmap.SizeInPixels.Width,
                                 h / (float)bitmap.SizeInPixels.Height),
            InterpolationMode = CanvasImageInterpolation.HighQualityCubic
        };

        var roundedMask = new CanvasCommandList(device);
        using (var maskDs = roundedMask.CreateDrawingSession())
        {
            maskDs.Clear(Microsoft.UI.Colors.Transparent);
            maskDs.FillRoundedRectangle(0, 0, w, h, radius, radius,
                                        Microsoft.UI.Colors.White);
        }

        var maskedImage = new AlphaMaskEffect
        {
            Source = scaleEffect,
            AlphaMask = roundedMask
        };

        var shadow = new ShadowEffect
        {
            Source = maskedImage,
            BlurAmount = 20f,
            ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0)
        };

        var shadowWithOffset = new Transform2DEffect
        {
            Source = shadow,
            TransformMatrix = Matrix3x2.CreateTranslation(4f, 6f)
        };

        var composite = new CompositeEffect();
        composite.Sources.Add(shadowWithOffset);
        composite.Sources.Add(maskedImage);

        var withOpacity = new OpacityEffect
        {
            Source = composite,
            Opacity = opacity
        };

        ds.DrawImage(withOpacity, (float)destRect.X, (float)destRect.Y);

        withOpacity.Dispose();
        composite.Dispose();
        shadowWithOffset.Dispose();
        shadow.Dispose();
        maskedImage.Dispose();
        roundedMask.Dispose();
        scaleEffect.Dispose();
    }

    // ── 图片加载与颜色提取 ───────────────────────────────────────────────

    private async Task LoadImageFromBytesAsync(byte[] imageBytes)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        bool isDark = IsDark;
        bool useImageDominantTheme = UseImageDominantTheme;

        try
        {
            var (weighted, resolvedIsDark, displayBitmap) = await Task.Run(async () =>
            {
                using var memStream = new MemoryStream(imageBytes, writable: false);
                using var rasStream = memStream.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(rasStream);

                // 颜色提取（缩略图）
                List<(Vector3 Color, int Population)> result;
                bool effectiveIsDark;
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

                // 解码显示用像素（最大 1024）
                const uint MaxDisplaySize = 1024;
                uint srcW = decoder.PixelWidth;
                uint srcH = decoder.PixelHeight;
                float scale = Math.Min(1f, Math.Min((float)MaxDisplaySize / srcW,
                                                     (float)MaxDisplaySize / srcH));
                uint dstW = Math.Max(1, (uint)(srcW * scale));
                uint dstH = Math.Max(1, (uint)(srcH * scale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = dstW,
                    ScaledHeight = dstH,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                rasStream.Seek(0);
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                var pixels = pixelData.DetachPixelData();
                return (result, effectiveIsDark, (pixels, dstW, dstH));

            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            // 回到 UI 线程创建 CanvasBitmap
            var (pixels, bmpW, bmpH) = displayBitmap;

            if (_effect == null)
            {
                // 设备尚未就绪，暂存像素等待 CreateResources 补建
                _pendingPixels = pixels;
                _pendingBmpW = (int)bmpW;
                _pendingBmpH = (int)bmpH;
            }
            else
            {
                var newBitmap = CanvasBitmap.CreateFromBytes(
                    canvas,
                    pixels,
                    (int)bmpW,
                    (int)bmpH,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);

                _pendingBitmap?.Dispose();
                _pendingBitmap = newBitmap;
                _hasPendingBitmap = true;
            }

            // 颜色过渡
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
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                cts.Dispose();
                _loadCts = null;
            }
        }
    }

    // ── 颜色算法（原封不动） ─────────────────────────────────────────────

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
        bool isDark,
        bool useImageDominantTheme)
    {
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

    // ── 资源释放 ────────────────────────────────────────────────────────

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose(true);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool dispose)
    {
        if (!dispose) return;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _effect?.Dispose();
        _effect = null;

        _currentBitmap?.Dispose();
        _currentBitmap = null;

        _nextBitmap?.Dispose();
        _nextBitmap = null;

        _pendingBitmap?.Dispose();
        _pendingBitmap = null;

        _pendingPixels = null;

        ThemeResolved = null;
    }
}