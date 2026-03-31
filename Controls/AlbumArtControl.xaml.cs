using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Controls;

public sealed partial class AlbumArtControl : UserControl, IDisposable
{
    // ── 依赖属性 ──────────────────────────────────────────────────────────

    public static readonly DependencyProperty ImageBytesProperty =
        DependencyProperty.Register(nameof(ImageBytes), typeof(byte[]),
            typeof(AlbumArtControl), new PropertyMetadata(null, OnImageBytesChanged));

    public byte[] ImageBytes
    {
        get => (byte[])GetValue(ImageBytesProperty);
        set => SetValue(ImageBytesProperty, value);
    }

    private static void OnImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (AlbumArtControl)d;
        if (!ctrl._isResourcesCreated) return;
        // IsActive=false 时不做任何加载，等激活时再由 OnIsActiveChanged 触发
        if (!ctrl.IsActive) return;

        var newBytes = e.NewValue as byte[];
        if (IsDuplicateAndUpdate(newBytes)) return;

        if (newBytes is { Length: > 0 })
            _ = ctrl.LoadBitmapAsync(newBytes);
        else
            _ = ctrl.LoadDefaultCoverAsync();
    }

    public static readonly DependencyProperty IsDarkProperty =
        DependencyProperty.Register(nameof(IsDark), typeof(bool),
            typeof(AlbumArtControl), new PropertyMetadata(true, OnIsDarkChanged));

    public bool IsDark
    {
        get => (bool)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    private static void OnIsDarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (AlbumArtControl)d;
        if (!ctrl._isResourcesCreated) return;
        if (!ctrl.IsActive) return;

        Invalidate();

        if (ctrl.ImageBytes is { Length: > 0 })
            _ = ctrl.LoadBitmapAsync(ctrl.ImageBytes);
        else
            _ = ctrl.LoadDefaultCoverAsync();
    }

    public static readonly DependencyProperty MarginTopRatioProperty =
        DependencyProperty.Register(nameof(MarginTopRatio), typeof(double),
            typeof(AlbumArtControl), new PropertyMetadata(20.0, OnLayoutChanged));
    public double MarginTopRatio
    {
        get => (double)GetValue(MarginTopRatioProperty);
        set => SetValue(MarginTopRatioProperty, value);
    }

    public static readonly DependencyProperty MarginBottomRatioProperty =
        DependencyProperty.Register(nameof(MarginBottomRatio), typeof(double),
            typeof(AlbumArtControl), new PropertyMetadata(20.0, OnLayoutChanged));
    public double MarginBottomRatio
    {
        get => (double)GetValue(MarginBottomRatioProperty);
        set => SetValue(MarginBottomRatioProperty, value);
    }

    public static readonly DependencyProperty MarginLeftRatioProperty =
        DependencyProperty.Register(nameof(MarginLeftRatio), typeof(double),
            typeof(AlbumArtControl), new PropertyMetadata(20.0, OnLayoutChanged));
    public double MarginLeftRatio
    {
        get => (double)GetValue(MarginLeftRatioProperty);
        set => SetValue(MarginLeftRatioProperty, value);
    }

    public static readonly DependencyProperty MarginRightRatioProperty =
        DependencyProperty.Register(nameof(MarginRightRatio), typeof(double),
            typeof(AlbumArtControl), new PropertyMetadata(20.0, OnLayoutChanged));
    public double MarginRightRatio
    {
        get => (double)GetValue(MarginRightRatioProperty);
        set => SetValue(MarginRightRatioProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double),
            typeof(AlbumArtControl), new PropertyMetadata(16.0, OnLayoutChanged));
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty IsShadowEnabledProperty =
        DependencyProperty.Register(nameof(IsShadowEnabled), typeof(bool),
            typeof(AlbumArtControl), new PropertyMetadata(true, OnLayoutChanged));
    public bool IsShadowEnabled
    {
        get => (bool)GetValue(IsShadowEnabledProperty);
        set => SetValue(IsShadowEnabledProperty, value);
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (AlbumArtControl)d;
        if (!ctrl._isResourcesCreated) return;
        if (!ctrl.IsActive) return;

        ctrl._maskInvalidated = true;
        ctrl.canvas.Invalidate();
    }

    // ── IsActive 依赖属性 ─────────────────────────────────────────────────
    //
    // 默认 false：控件创建后不执行任何加载与绘制，直到外部显式置为 true。
    // 置为 true：按当前 ImageBytes 触发一次完整加载流程。
    // 置为 false：立即停止 FadeTimer，令下一帧 Draw 直接 return；
    //             保留 _currentBitmap 以便重新激活时可快速恢复（无需重新解码）。
    //             若需彻底释放 GPU 资源，调用方可同时设 Visibility=Collapsed。

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool),
            typeof(AlbumArtControl), new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (AlbumArtControl)d;
        if (!ctrl._isResourcesCreated) return;

        if ((bool)e.NewValue)
        {
            // 激活：重新触发加载（去重逻辑会过滤掉未变化的图片）
            if (ctrl.ImageBytes is { Length: > 0 } bytes)
                _ = ctrl.LoadBitmapAsync(bytes);
            else
                _ = ctrl.LoadDefaultCoverAsync();
        }
        else
        {
            // 停用：停止 Timer，取消正在进行的加载，触发一次 Draw（Draw 内直接 return）
            ctrl._loadCts?.Cancel();
            ctrl._fadeTimer?.Stop();
            ctrl.canvas.Invalidate();
        }
    }

    // ── 私有字段 ──────────────────────────────────────────────────────────

    private CanvasBitmap? _currentBitmap;
    private float _currentAlpha = 0f;

    private CanvasBitmap? _incomingBitmap;
    private float _incomingAlpha = 0f;

    private bool _isFading = false;
    private const float FadeSpeed = 1.25f;

    private CanvasBitmap? _queuedBitmap;

    private readonly Queue<CanvasBitmap> _disposeQueue = new();

    private CancellationTokenSource? _loadCts;
    private bool _isResourcesCreated = false;

    private DispatcherTimer? _fadeTimer;
    private DateTime _lastTick;
    private const float HardMaxSize = 1536f;
    private static long _lastLength = -1;
    private static int _lastHash;

    // ── 持久化的圆角遮罩 CanvasCommandList ───────────────────────────────

    private CanvasCommandList? _maskCL;
    private (float w, float h, float radius) _maskSize;
    private bool _maskInvalidated = false;

    // ── 去重 ──────────────────────────────────────────────────────────────

    public static bool IsDuplicateAndUpdate(byte[]? newBytes)
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

    public static void Invalidate()
    {
        _lastLength = -1;
        _lastHash = 0;
    }

    // ── 构造函数 ──────────────────────────────────────────────────────────

    public AlbumArtControl()
    {
        InitializeComponent();
        RegisterCanvasEvents();
        Unloaded += (_, _) => Dispose(true);
    }

    // ── Canvas 事件 ───────────────────────────────────────────────────────

    private void RegisterCanvasEvents()
    {
        canvas.CreateResources += async (s, e) =>
        {
            _isResourcesCreated = true;

            // IsActive=false 时跳过初始加载，等激活时由 OnIsActiveChanged 触发
            if (!IsActive) return;

            if (ImageBytes is { Length: > 0 } bytes)
                await LoadBitmapAsync(bytes);
            else
                await LoadDefaultCoverAsync();
        };

        canvas.Draw += (s, e) =>
        {
            DrawImageLayer(e.DrawingSession);
        };
    }

    // ── 淡入淡出驱动 ──────────────────────────────────────────────────────

    private void EnsureFadeTimer()
    {
        if (_fadeTimer == null)
        {
            _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.67) };
            _fadeTimer.Tick += OnFadeTick;
        }
        if (!_fadeTimer.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            _fadeTimer.Start();
        }
    }

    private void OnFadeTick(object? sender, object e)
    {
        var now = DateTime.UtcNow;
        float delta = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;

        UpdateFadeState(delta);
        canvas.Invalidate();

        if (!_isFading && _queuedBitmap == null)
        {
            _fadeTimer!.Stop();
            FlushDisposeQueue();
        }
    }

    // ── 核心状态机 ────────────────────────────────────────────────────────

    private void EnqueueBitmap(CanvasBitmap newBitmap)
    {
        if (_isFading)
        {
            if (_queuedBitmap != null)
                _disposeQueue.Enqueue(_queuedBitmap);
            _queuedBitmap = newBitmap;
        }
        else
        {
            StartTransition(newBitmap);
        }

        EnsureFadeTimer();
    }

    private void StartTransition(CanvasBitmap newBitmap)
    {
        if (_incomingBitmap != null)
        {
            if (_currentBitmap != null)
                _disposeQueue.Enqueue(_currentBitmap);
            _currentBitmap = _incomingBitmap;
            _currentAlpha = _incomingAlpha;
            _incomingBitmap = null;
            _incomingAlpha = 0f;
        }

        _incomingBitmap = newBitmap;
        _incomingAlpha = 0f;
        _isFading = true;
    }

    private void UpdateFadeState(float delta)
    {
        FlushDisposeQueue();

        if (!_isFading) return;

        if (_currentBitmap != null)
            _currentAlpha = Math.Max(0f, _currentAlpha - delta * FadeSpeed);

        if (_incomingBitmap != null)
            _incomingAlpha = Math.Min(1f, _incomingAlpha + delta * FadeSpeed);

        if (_incomingAlpha >= 1f)
        {
            if (_currentBitmap != null)
                _disposeQueue.Enqueue(_currentBitmap);

            _currentBitmap = _incomingBitmap;
            _currentAlpha = 1f;
            _incomingBitmap = null;
            _incomingAlpha = 0f;
            _isFading = false;

            if (_queuedBitmap != null)
            {
                var next = _queuedBitmap;
                _queuedBitmap = null;
                StartTransition(next);
            }
        }
    }

    private void FlushDisposeQueue()
    {
        while (_disposeQueue.TryDequeue(out var bmp))
        {
            if (bmp != null && bmp != _currentBitmap && bmp != _incomingBitmap && bmp != _queuedBitmap)
                bmp.Dispose();
        }
    }

    // ── 核心加载逻辑 ──────────────────────────────────────────────────────

    public async Task LoadBitmapAsync(byte[]? imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
        {
            await LoadDefaultCoverAsync();
            return;
        }

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        try
        {
            var (pixels, bmpW, bmpH) = await Task.Run(async () =>
            {
                using var mem = new MemoryStream(imageBytes, writable: false);
                using var ras = mem.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(ras);

                uint srcW = decoder.PixelWidth;
                uint srcH = decoder.PixelHeight;

                float sc = Math.Min(1f, Math.Min(HardMaxSize / srcW, HardMaxSize / srcH));
                uint dstW = Math.Max(1, (uint)(srcW * sc));
                uint dstH = Math.Max(1, (uint)(srcH * sc));

                cts.Token.ThrowIfCancellationRequested();

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform { ScaledWidth = dstW, ScaledHeight = dstH, InterpolationMode = BitmapInterpolationMode.Fant },
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                return (pixelData.DetachPixelData(), dstW, dstH);
            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();
            if (canvas.Device == null) return;

            var bmp = CanvasBitmap.CreateFromBytes(
                canvas, pixels, (int)bmpW, (int)bmpH,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);

            pixels = null;

            EnqueueBitmap(bmp);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Invalidate();
            await LoadDefaultCoverAsync();
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            cts.Dispose();
        }
    }

    public async Task LoadDefaultCoverAsync()
    {
        try
        {
            if (canvas.Device == null) return;

            string fileName = IsDark ? "default_cover_black.png" : "default_cover_white.png";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();

            var bmp = await CanvasBitmap.LoadAsync(canvas, stream);
            EnqueueBitmap(bmp);
        }
        catch { /* 忽略 IO 异常 */ }
    }

    // ── 绘制 ──────────────────────────────────────────────────────────────

    private void DrawImageLayer(CanvasDrawingSession ds)
    {
        // IsActive=false：跳过所有 GPU 计算，直接返回（canvas 呈现透明空白帧）
        if (!IsActive) return;

        float canvasW = (float)canvas.Size.Width;
        float canvasH = (float)canvas.Size.Height;
        if (canvasW <= 0 || canvasH <= 0) return;

        float squareSize = Math.Min(canvasW, canvasH);
        float squareX = (canvasW - squareSize) * 0.5f;
        float squareY = (canvasH - squareSize) * 0.5f;

        float padTop = (float)MarginTopRatio;
        float padBottom = (float)MarginBottomRatio;
        float padLeft = (float)MarginLeftRatio;
        float padRight = (float)MarginRightRatio;

        float contentX = squareX + padLeft;
        float contentY = squareY + padTop;
        float contentW = squareSize - padLeft - padRight;
        float contentH = squareSize - padTop - padBottom;
        if (contentW <= 0 || contentH <= 0) return;

        CanvasBitmap? refBmp = _incomingBitmap ?? _currentBitmap;
        if (refBmp == null) return;

        var destRect = CalcDestRect(refBmp, contentX, contentY, contentW, contentH);
        float radius = (float)CornerRadius;
        float w = (float)destRect.Width;
        float h = (float)destRect.Height;

        EnsureMaskCommandList(ds.Device, w, h, radius);

        if (_currentBitmap != null && _currentAlpha > 0f)
            TryDrawRounded(ds, _currentBitmap, destRect, radius, _currentAlpha);

        if (_incomingBitmap != null && _incomingAlpha > 0f)
            TryDrawRounded(ds, _incomingBitmap, destRect, radius, _incomingAlpha);
    }

    private void EnsureMaskCommandList(CanvasDevice device, float w, float h, float radius)
    {
        if (!_maskInvalidated
            && _maskCL != null
            && MathF.Abs(_maskSize.w - w) < 0.5f
            && MathF.Abs(_maskSize.h - h) < 0.5f
            && MathF.Abs(_maskSize.radius - radius) < 0.5f)
        {
            return;
        }

        _maskCL?.Dispose();
        _maskCL = new CanvasCommandList(device);
        using (var maskDs = _maskCL.CreateDrawingSession())
        {
            maskDs.FillRoundedRectangle(0, 0, w, h, radius, radius, Microsoft.UI.Colors.White);
        }

        _maskSize = (w, h, radius);
        _maskInvalidated = false;
    }

    private static Windows.Foundation.Rect CalcDestRect(
        CanvasBitmap bmp, float cx, float cy, float cw, float ch)
    {
        float imgW = bmp.SizeInPixels.Width;
        float imgH = bmp.SizeInPixels.Height;
        if (imgW <= 0 || imgH <= 0) return new(cx, cy, cw, ch);

        float aspect = imgW / imgH;
        float drawW, drawH;
        if (aspect >= cw / ch) { drawW = cw; drawH = drawW / aspect; }
        else { drawH = ch; drawW = drawH * aspect; }

        return new(cx + (cw - drawW) * 0.5f, cy + (ch - drawH) * 0.5f, drawW, drawH);
    }

    private void TryDrawRounded(
        CanvasDrawingSession ds, CanvasBitmap bmp,
        Windows.Foundation.Rect dest, float radius, float opacity)
    {
        try { DrawRoundedImageWithShadow(ds, bmp, dest, radius, opacity); }
        catch { /* device lost 等，忽略本帧 */ }
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

        using var scale = new ScaleEffect
        {
            Source = bitmap,
            Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                 h / bitmap.SizeInPixels.Height),
            InterpolationMode = CanvasImageInterpolation.HighQualityCubic
        };

        using var masked = new AlphaMaskEffect { Source = scale, AlphaMask = _maskCL };

        using var shadow = new ShadowEffect
        {
            Source = masked,
            BlurAmount = 10f,
            ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0)
        };
        using var shadowOffset = new Transform2DEffect
        {
            Source = shadow,
            TransformMatrix = Matrix3x2.CreateTranslation(2f, 3f)
        };

        using var composite = new CompositeEffect();
        composite.Sources.Add(shadowOffset);
        composite.Sources.Add(masked);

        using var withOpacity = new OpacityEffect { Source = composite, Opacity = opacity };

        ds.DrawImage(withOpacity, (float)destRect.X, (float)destRect.Y);
    }

    // ── 释放 ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!disposing) return;

        _fadeTimer?.Stop();
        _fadeTimer = null;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _maskCL?.Dispose(); _maskCL = null;

        _currentBitmap?.Dispose(); _currentBitmap = null;
        _incomingBitmap?.Dispose(); _incomingBitmap = null;
        _queuedBitmap?.Dispose(); _queuedBitmap = null;

        while (_disposeQueue.TryDequeue(out var b))
            b.Dispose();
    }
}