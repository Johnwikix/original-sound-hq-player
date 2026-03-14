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
                _c1 = _target1; _c2 = _target2;
                _c3 = _target3; _c4 = _target4;
                _transitionProgress = 1f;
            }
            else
            {
                ApplyDefaultColors();
                _c1 = _target1; _c2 = _target2;
                _c3 = _target3; _c4 = _target4;
                _transitionProgress = 1f;
            }

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
            var colors = await Task.Run(async () =>
            {
                using var memStream = new MemoryStream(imageBytes, writable: false);
                using var rasStream = memStream.AsRandomAccessStream();

                var decoder = await BitmapDecoder.CreateAsync(rasStream);

                List<Vector3> result;
                using (var image = await ConvertToImageSharpAsync(decoder))
                {
                    cts.Token.ThrowIfCancellationRequested();

                    var thief = new ColorThief.ImageSharp.ColorThief();

                    // 多取候选色，增加容错
                    var palette = thief.GetPalette(image, 8, 10, isDark);

                    // 优先使用符合明暗要求的颜色，按 Population 降序
                    var preferred = palette
                        .Where(t => t.IsDark == isDark)
                        .OrderByDescending(t => t.Population)
                        .Select(t => new Vector3(
                            t.Color.R / 255f,
                            t.Color.G / 255f,
                            t.Color.B / 255f))
                        .ToList();

                    // 不足时用原图其余颜色补充，不强制过滤，保留色调
                    var fallback = palette
                        .Where(t => t.IsDark != isDark)
                        .OrderByDescending(t => t.Population)
                        .Select(t => new Vector3(
                            t.Color.R / 255f,
                            t.Color.G / 255f,
                            t.Color.B / 255f));

                    result = preferred.Concat(fallback).Take(4).ToList();
                }

                return result;

            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            // 真正没有任何颜色时的兜底，用中性深/浅灰，而非极端黑白
            if (colors.Count == 0)
                colors.Add(isDark ? new Vector3(0.05f) : new Vector3(0.95f));

            PadColorsToFour(colors, isDark);

            // 强制将亮度映射到目标区间，保留原图色相与饱和度
            for (int i = 0; i < colors.Count; i++)
                colors[i] = EnforceLuminance(colors[i], isDark);

            _target1 = colors[0];
            _target2 = colors[1];
            _target3 = colors[2];
            _target4 = colors[3];
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
    /// 将颜色列表补全至 4 个。
    /// 基于已有颜色微扰，保留色调，不向明/暗方向强推。
    /// </summary>
    private static void PadColorsToFour(List<Vector3> colors, bool isDark)
    {
        var rng = new Random(colors[0].GetHashCode());
        Vector3 min = new Vector3(0.05f);
        Vector3 max = new Vector3(0.95f);

        while (colors.Count < 4)
        {
            // 从已有颜色随机取一个作为基础，加小幅随机扰动，保持整体色调一致
            var baseColor = colors[rng.Next(colors.Count)];
            const float jitter = 0.06f;
            var next = new Vector3(
                baseColor.X + (float)(rng.NextDouble() - 0.5) * jitter * 2,
                baseColor.Y + (float)(rng.NextDouble() - 0.5) * jitter * 2,
                baseColor.Z + (float)(rng.NextDouble() - 0.5) * jitter * 2);
            colors.Add(Vector3.Clamp(next, min, max));
        }
    }

    /// <summary>
    /// 保留色相和饱和度，强制将感知亮度压入目标区间。
    /// isDark=true  → [0.03, 0.32]  足够深，白色文字清晰可读
    /// isDark=false → [0.68, 0.97]  足够亮，深色文字清晰可读
    /// </summary>
    private static Vector3 EnforceLuminance(Vector3 rgb, bool isDark)
    {
        // ITU-R BT.709 感知亮度
        float lum = rgb.X * 0.2126f + rgb.Y * 0.7152f + rgb.Z * 0.0722f;

        float targetMin = isDark ? 0.03f : 0.68f;
        float targetMax = isDark ? 0.32f : 0.97f;

        // 已在目标区间内，不做调整
        if (lum >= targetMin && lum <= targetMax)
            return rgb;

        float targetLum = Math.Clamp(lum, targetMin, targetMax);

        // 纯黑兜底：直接返回目标亮度的中性灰
        if (lum < 1e-5f)
            return new Vector3(targetLum);

        // 等比缩放 RGB：保持色相/饱和度，仅调整亮度
        float scale = targetLum / lum;
        return Vector3.Clamp(rgb * scale, new Vector3(0.01f), new Vector3(0.99f));
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