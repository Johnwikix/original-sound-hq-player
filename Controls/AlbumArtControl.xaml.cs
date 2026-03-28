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
        if (e.NewValue is byte[] bytes && bytes.Length > 0)
            _ = ctrl.LoadBitmapAsync(bytes);
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
        _ = ctrl.LoadBitmapAsync(ctrl.ImageBytes);
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
        ctrl.canvas.Invalidate();
    }

    // ── 私有字段 ──────────────────────────────────────────────────────────

    // 当前显示的位图及其 alpha（始终 0→1 稳定）
    private CanvasBitmap? _currentBitmap;
    private float _currentAlpha = 0f;

    // 正在淡入的目标位图
    private CanvasBitmap? _incomingBitmap;
    private float _incomingAlpha = 0f;

    private bool _isFading = false;
    private const float FadeSpeed = 1.25f;   // 每秒 alpha 变化量，约 0.4s 完成

    // 快速切换时的"最新待显示"位图，只保留最新的一张
    private CanvasBitmap? _queuedBitmap;

    // 延迟释放队列：等过渡完成后再 Dispose，避免 GPU 仍在使用时释放
    private readonly Queue<CanvasBitmap> _disposeQueue = new();

    private CancellationTokenSource? _loadCts;
    private bool _isResourcesCreated = false;

    // 淡入淡出驱动
    private DispatcherTimer? _fadeTimer;
    private DateTime _lastTick;

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
            if (ImageBytes is { Length: > 0 } bytes)
                await LoadBitmapAsync(bytes);
            else
                await LoadDefaultCoverAsync();
        };

        canvas.Draw += (s, e) =>
        {
            e.DrawingSession.Clear(Microsoft.UI.Colors.Transparent);
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

        // 先 Invalidate 触发绘制，绘制完成后 GPU 才真正用完旧帧
        // 释放在下一 Tick 开头，给 GPU 留一帧缓冲
        canvas.Invalidate();

        if (!_isFading && _queuedBitmap == null)
        {
            _fadeTimer!.Stop();
            // Timer 停止后再做一次延迟释放清理
            FlushDisposeQueue();
        }
    }

    // ── 核心状态机 ────────────────────────────────────────────────────────

    /// <summary>
    /// 推入新位图，开始淡入过渡。
    /// 必须在 UI 线程调用。
    /// </summary>
    private void EnqueueBitmap(CanvasBitmap newBitmap)
    {
        if (_isFading)
        {
            // 正在过渡中：把旧的 queued 放入延迟释放队列，新的顶上
            if (_queuedBitmap != null)
                _disposeQueue.Enqueue(_queuedBitmap);
            _queuedBitmap = newBitmap;
        }
        else
        {
            // 当前静止：直接开始新过渡
            StartTransition(newBitmap);
        }

        EnsureFadeTimer();
    }

    private void StartTransition(CanvasBitmap newBitmap)
    {
        // 如果之前有正在淡入但还未完成的 incoming，将其提升为 current
        // （保证 current 永远是上一个"稳定"画面，不跳帧）
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
        // 每 Tick 开头释放队列中已安全过期的位图（上上帧已不再绘制）
        FlushDisposeQueue();

        if (!_isFading) return;

        // 当前画面淡出
        if (_currentBitmap != null)
            _currentAlpha = Math.Max(0f, _currentAlpha - delta * FadeSpeed);

        // 新画面淡入
        if (_incomingBitmap != null)
            _incomingAlpha = Math.Min(1f, _incomingAlpha + delta * FadeSpeed);

        // 过渡完成
        if (_incomingAlpha >= 1f)
        {
            if (_currentBitmap != null)
                _disposeQueue.Enqueue(_currentBitmap);

            _currentBitmap = _incomingBitmap;
            _currentAlpha = 1f;
            _incomingBitmap = null;
            _incomingAlpha = 0f;
            _isFading = false;

            // 如果队列中还有等待的新图，立刻开始下一段过渡
            if (_queuedBitmap != null)
            {
                var next = _queuedBitmap;
                _queuedBitmap = null;
                StartTransition(next);
            }
        }
    }

    // 每次只释放一个，给 GPU 留足够的缓冲帧
    private void FlushDisposeQueue()
    {
        if (_disposeQueue.TryDequeue(out var bmp))
            bmp.Dispose();
    }

    // ── 位图加载 ──────────────────────────────────────────────────────────

    public async Task LoadBitmapAsync(byte[]? imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0) return;

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        try
        {
            // 后台线程只做解码，不碰任何 CanvasDevice / CanvasBitmap
            var (pixels, bmpW, bmpH) = await Task.Run(async () =>
            {
                using var mem = new MemoryStream(imageBytes, writable: false);
                using var ras = mem.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(ras);

                const uint MaxDisplaySize = 1280;
                uint srcW = decoder.PixelWidth;
                uint srcH = decoder.PixelHeight;
                float sc = Math.Min(1f, Math.Min((float)MaxDisplaySize / srcW,
                                                  (float)MaxDisplaySize / srcH));
                uint dstW = Math.Max(1, (uint)(srcW * sc));
                uint dstH = Math.Max(1, (uint)(srcH * sc));

                cts.Token.ThrowIfCancellationRequested();

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform
                    {
                        ScaledWidth = dstW,
                        ScaledHeight = dstH,
                        InterpolationMode = BitmapInterpolationMode.Fant
                    },
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                return (pixelData.DetachPixelData(), dstW, dstH);
            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            // CanvasBitmap 必须在 UI 线程 / CanvasDevice 线程创建
            // 此处已回到 UI 线程（await 之后）
            if (canvas.Device == null) return;

            var bmp = CanvasBitmap.CreateFromBytes(
                canvas, pixels, (int)bmpW, (int)bmpH,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);

            EnqueueBitmap(bmp);
        }
        catch (OperationCanceledException) { /* 正常取消 */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadBitmap Error: {ex.Message}");
            await LoadDefaultCoverAsync();
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                _loadCts = null;
            cts.Dispose();
        }
    }

    public async Task LoadDefaultCoverAsync()
    {
        try
        {
            string path = IsDark
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets\\default_cover_black.png")
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets\\default_cover_white.png");

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();

            if (canvas.Device == null) return;

            var bmp = await CanvasBitmap.LoadAsync(canvas, stream);
            EnqueueBitmap(bmp);
        }
        catch { }
    }

    // ── 绘制 ──────────────────────────────────────────────────────────────

    private void DrawImageLayer(CanvasDrawingSession ds)
    {
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

        // 用 incoming（如有）决定目标尺寸比例，保证画面不跳变
        CanvasBitmap? refBmp = _incomingBitmap ?? _currentBitmap;
        if (refBmp == null) return;

        var destRect = CalcDestRect(refBmp, contentX, contentY, contentW, contentH);
        float radius = (float)CornerRadius;

        // 先画正在淡出的旧图
        if (_currentBitmap != null && _currentAlpha > 0f)
            TryDrawRounded(ds, _currentBitmap, destRect, radius, _currentAlpha);

        // 再画正在淡入的新图（叠在上面）
        if (_incomingBitmap != null && _incomingAlpha > 0f)
            TryDrawRounded(ds, _incomingBitmap, destRect, radius, _incomingAlpha);
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

    private static void TryDrawRounded(
        CanvasDrawingSession ds, CanvasBitmap bmp,
        Windows.Foundation.Rect dest, float radius, float opacity)
    {
        try { DrawRoundedImageWithShadow(ds, bmp, dest, radius, opacity); }
        catch { /* device lost 等，忽略本帧 */ }
    }

    private static void DrawRoundedImageWithShadow(
        CanvasDrawingSession ds,
        CanvasBitmap bitmap,
        Windows.Foundation.Rect destRect,
        float radius,
        float opacity)
    {
        float w = (float)destRect.Width;
        float h = (float)destRect.Height;
        if (w <= 0 || h <= 0) return;

        // ScaleEffect：将位图缩放到目标尺寸
        using var scale = new ScaleEffect
        {
            Source = bitmap,
            Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                h / bitmap.SizeInPixels.Height),
            InterpolationMode = CanvasImageInterpolation.HighQualityCubic
        };

        // 圆角遮罩
        using var maskCL = new CanvasCommandList(ds.Device);
        using (var maskDs = maskCL.CreateDrawingSession())
        {
            maskDs.Clear(Microsoft.UI.Colors.Transparent);
            maskDs.FillRoundedRectangle(0, 0, w, h, radius, radius, Microsoft.UI.Colors.White);
        }

        using var masked = new AlphaMaskEffect { Source = scale, AlphaMask = maskCL };

        // 阴影
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

        // 合成：阴影在下，图像在上
        using var composite = new CompositeEffect();
        composite.Sources.Add(shadowOffset);
        composite.Sources.Add(masked);

        // 统一 opacity
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

        _currentBitmap?.Dispose(); _currentBitmap = null;
        _incomingBitmap?.Dispose(); _incomingBitmap = null;
        _queuedBitmap?.Dispose(); _queuedBitmap = null;

        while (_disposeQueue.TryDequeue(out var b))
            b.Dispose();
    }
}