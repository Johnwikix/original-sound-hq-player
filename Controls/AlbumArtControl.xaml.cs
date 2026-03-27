using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

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

    // ── 依赖属性（布局 + 阴影） ────────────────────────────────────────────

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

    private CanvasBitmap? _currentBitmap;
    private float _imgAlpha;

    private CanvasBitmap? _nextBitmap;
    private float _imgNextAlpha;

    private bool _isCrossFading;
    private const float ImageFadeSpeed = 2.0f;

    private CanvasBitmap? _pendingBitmap;
    private bool _hasPendingBitmap;

    private byte[]? _pendingPixels;
    private int _pendingBmpW;
    private int _pendingBmpH;

    private CanvasBitmap? _disposeBitmap1;
    private CanvasBitmap? _disposeBitmap2;

    private CancellationTokenSource? _loadCts;

    // 淡入淡出驱动
    private DispatcherTimer? _fadeTimer;
    private DateTime _lastTick;
    private bool _isResourcesCreated = false;
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
        // CreateResources：设备就绪后补建挂起的像素数据，并加载初始封面
        canvas.CreateResources += async (s, e) =>
        {
            if (_pendingPixels != null)
            {
                var bmp = CanvasBitmap.CreateFromBytes(
                    canvas, _pendingPixels,
                    _pendingBmpW, _pendingBmpH,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
                _pendingBitmap?.Dispose();
                _pendingBitmap = bmp;
                _hasPendingBitmap = true;
                _pendingPixels = null;

                // 刚补建了挂起位图，需要触发一次淡入
                StartFadeTimer();
            }            
            if (ImageBytes is { Length: > 0 } bytes)
                await LoadBitmapAsync(bytes);
            else
                await LoadDefaultCoverAsync();
            _isResourcesCreated = true;
        };

        // Draw：只负责绘制当前帧，不含任何状态更新
        canvas.Draw += (s, e) =>
        {
            e.DrawingSession.Clear(Microsoft.UI.Colors.Transparent);
            DrawImageLayer(e.DrawingSession);
        };
    }

    // ── 淡入淡出驱动（DispatcherTimer）────────────────────────────────────

    private void StartFadeTimer()
    {
        if (_fadeTimer?.IsEnabled == true) return;

        _lastTick = DateTime.UtcNow;

        if (_fadeTimer == null)
        {
            _fadeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60 fps
            };
            _fadeTimer.Tick += OnFadeTick;
        }

        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, object e)
    {
        var now = DateTime.UtcNow;
        float delta = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;

        FlushDisposeQueue();
        UpdateImageFade(delta);
        canvas.Invalidate();

        // 过渡完成后停止 Timer，画面静止时不再消耗资源
        if (!_isCrossFading)
            _fadeTimer!.Stop();
    }

    // ── 位图加载 ──────────────────────────────────────────────────────────

    public async Task LoadBitmapAsync(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0) return;

        // 【关键修复】只取消，不在此处 Dispose，防止杀掉正在运行的旧 Task
        _loadCts?.Cancel();

        var cts = new CancellationTokenSource();
        _loadCts = cts;

        try
        {
            // 这里的 Task.Run 闭包捕获的是局部变量 cts，它是安全的
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

                var transform = new BitmapTransform
                {
                    ScaledWidth = dstW,
                    ScaledHeight = dstH,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                // 在访问 Token 前，它保证是未被 Dispose 的
                cts.Token.ThrowIfCancellationRequested();

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                return (pixelData.DetachPixelData(), dstW, dstH);
            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            SetPendingPixels(pixels, (int)bmpW, (int)bmpH);
        }
        catch (OperationCanceledException) { /* 正常取消 */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadBitmap Error: {ex.Message}");
            await LoadDefaultCoverAsync();
        }
        finally
        {
            // 【关键修复】只有当全局变量还是自己时，才清理全局引用
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
            }
            // 每个任务负责释放自己创建的 cts
            cts.Dispose();
        }
    }

    public async Task LoadDefaultCoverAsync()
    {
        try
        {
            string url = IsDark
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets\\default_cover_black.png")
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets\\default_cover_white.png");

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(url);
            using var stream = await file.OpenReadAsync();

            if (canvas.Device != null)
            {
                var bmp = await CanvasBitmap.LoadAsync(canvas, stream);
                _pendingBitmap?.Dispose();
                _pendingBitmap = bmp;
                _hasPendingBitmap = true;
                StartFadeTimer();
            }
        }
        catch { }
    }

    // ── 像素暂存（设备未就绪时）───────────────────────────────────────────

    private void SetPendingPixels(byte[] pixels, int w, int h)
    {
        if (canvas.Device != null)
        {
            var bmp = CanvasBitmap.CreateFromBytes(
                canvas, pixels, w, h,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _pendingBitmap?.Dispose();
            _pendingBitmap = bmp;
            _hasPendingBitmap = true;
            StartFadeTimer();  // 有新位图，启动淡入过渡
        }
        else
        {
            _pendingPixels = pixels;
            _pendingBmpW = w;
            _pendingBmpH = h;
        }
    }

    // ── 淡入淡出逻辑 ──────────────────────────────────────────────────────

    private void FlushDisposeQueue()
    {
        _disposeBitmap1?.Dispose();
        _disposeBitmap1 = _disposeBitmap2;
        _disposeBitmap2 = null;
    }

    private void UpdateImageFade(float delta)
    {
        if (_hasPendingBitmap)
        {
            _hasPendingBitmap = false;

            if (_isCrossFading && _nextBitmap != null)
            {
                if (_currentBitmap != null)
                {
                    _disposeBitmap2 = _disposeBitmap1;
                    _disposeBitmap1 = _currentBitmap;
                }
                _currentBitmap = _nextBitmap;
                _imgAlpha = _imgNextAlpha;
                _nextBitmap = null;
                _imgNextAlpha = 0f;
            }

            if (_nextBitmap != null)
            {
                _disposeBitmap2 = _disposeBitmap1;
                _disposeBitmap1 = _nextBitmap;
            }

            _nextBitmap = _pendingBitmap;
            _pendingBitmap = null;
            _imgNextAlpha = 0f;
            _isCrossFading = true;
        }

        if (!_isCrossFading) return;

        if (_currentBitmap != null)
            _imgAlpha = Math.Max(0f, _imgAlpha - delta * ImageFadeSpeed);

        if (_nextBitmap != null)
            _imgNextAlpha = Math.Min(1f, _imgNextAlpha + delta * ImageFadeSpeed);

        if (_imgNextAlpha >= 1f)
        {
            if (_currentBitmap != null)
            {
                _disposeBitmap2 = _disposeBitmap1;
                _disposeBitmap1 = _currentBitmap;
            }
            _currentBitmap = _nextBitmap;
            _imgAlpha = 1f;
            _nextBitmap = null;
            _imgNextAlpha = 0f;
            _isCrossFading = false;
        }
    }

    // ── 绘制 ──────────────────────────────────────────────────────────────

    private void DrawImageLayer(CanvasDrawingSession ds)
    {
        float canvasW = (float)canvas.Size.Width;
        float canvasH = (float)canvas.Size.Height;
        if (canvasW <= 0 || canvasH <= 0) return;

        float regionW = canvasW;
        float regionH = canvasH;
        float squareSize = Math.Min(regionW, regionH);
        float squareX = (regionW - squareSize) * 0.5f;
        float squareY = (regionH - squareSize) * 0.5f;

        float padTop = (float)(MarginTopRatio);
        float padBottom = (float)(MarginBottomRatio);
        float padLeft = (float)(MarginLeftRatio);
        float padRight = (float)(MarginRightRatio);

        float contentX = squareX + padLeft;
        float contentY = squareY + padTop;
        float contentW = squareSize - padLeft - padRight;
        float contentH = squareSize - padTop - padBottom;
        if (contentW <= 0 || contentH <= 0) return;

        var current = _currentBitmap;
        var next = _nextBitmap;

        CanvasBitmap? refBitmap = next ?? current;
        if (refBitmap == null) return;

        float imgW, imgH;
        try { imgW = refBitmap.SizeInPixels.Width; imgH = refBitmap.SizeInPixels.Height; }
        catch { return; }
        if (imgW <= 0 || imgH <= 0) return;

        float imgAspect = imgW / imgH;
        float drawW, drawH;
        if (imgAspect >= contentW / contentH)
        { drawW = contentW; drawH = drawW / imgAspect; }
        else
        { drawH = contentH; drawW = drawH * imgAspect; }

        float drawX = contentX + (contentW - drawW) * 0.5f;
        float drawY = contentY + (contentH - drawH) * 0.5f;

        var destRect = new Windows.Foundation.Rect(drawX, drawY, drawW, drawH);
        float radius = (float)CornerRadius;

        if (current != null && _imgAlpha > 0f)
        {
            try { DrawRoundedImageWithShadow(ds, current, destRect, radius, _imgAlpha, IsShadowEnabled); }
            catch { }
        }

        if (next != null && _imgNextAlpha > 0f)
        {
            try { DrawRoundedImageWithShadow(ds, next, destRect, radius, _imgNextAlpha, IsShadowEnabled); }
            catch { }
        }
    }

    private static void DrawRoundedImageWithShadow(
    CanvasDrawingSession ds,
    CanvasBitmap bitmap,
    Windows.Foundation.Rect destRect,
    float radius,
    float opacity,
    bool shadowEnabled)          // ← 新增参数
    {
        float w = (float)destRect.Width;
        float h = (float)destRect.Height;
        if (w <= 0 || h <= 0) return;

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
            maskDs.FillRoundedRectangle(0, 0, w, h, radius, radius, Microsoft.UI.Colors.White);
        }

        var maskedImage = new AlphaMaskEffect { Source = scaleEffect, AlphaMask = roundedMask };

        ICanvasImage root;                               // 最终送入 CompositeEffect 的图层

        ShadowEffect? shadow = null;
        Transform2DEffect? shadowOffset = null;

        if (shadowEnabled)
        {
            shadow = new ShadowEffect
            {
                Source = maskedImage,
                BlurAmount = 10f,
                ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0)
            };
            shadowOffset = new Transform2DEffect
            {
                Source = shadow,
                TransformMatrix = Matrix3x2.CreateTranslation(2f, 3f)
            };

            var composite = new CompositeEffect();
            composite.Sources.Add(shadowOffset);
            composite.Sources.Add(maskedImage);
            root = composite;
        }
        else
        {
            root = maskedImage;
        }

        var withOpacity = new OpacityEffect { Source = root, Opacity = opacity };
        ds.DrawImage(withOpacity, (float)destRect.X, (float)destRect.Y);

        withOpacity.Dispose();
        if (root is CompositeEffect c) c.Dispose();
        shadowOffset?.Dispose();
        shadow?.Dispose();
        maskedImage.Dispose();
        roundedMask.Dispose();
        scaleEffect.Dispose();
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

        _fadeTimer?.Stop();
        _fadeTimer = null;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _currentBitmap?.Dispose(); _currentBitmap = null;
        _nextBitmap?.Dispose(); _nextBitmap = null;
        _pendingBitmap?.Dispose(); _pendingBitmap = null;
        _disposeBitmap1?.Dispose(); _disposeBitmap1 = null;
        _disposeBitmap2?.Dispose(); _disposeBitmap2 = null;
        _pendingPixels = null;
    }
}
