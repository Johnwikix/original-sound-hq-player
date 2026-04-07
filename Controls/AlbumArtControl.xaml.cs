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
        if (!ctrl.IsActive) return;

        var newBytes = e.NewValue as byte[];
        if (ctrl.IsDuplicateAndUpdate(newBytes)) return;

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

        ctrl.InvalidateDedup();

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
            typeof(AlbumArtControl), new PropertyMetadata(true, OnShadowEnabledChanged));
    public bool IsShadowEnabled
    {
        get => (bool)GetValue(IsShadowEnabledProperty);
        set => SetValue(IsShadowEnabledProperty, value);
    }

    private static void OnShadowEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (AlbumArtControl)d;
        if (!ctrl._isResourcesCreated) return;
        // Shadow 变化需要重新烘焙所有 RenderTarget
        ctrl._rtInvalidated = true;
        ctrl.canvas.Invalidate();
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (AlbumArtControl)d;
        if (!ctrl._isResourcesCreated) return;
        if (!ctrl.IsActive) return;

        ctrl._maskInvalidated = true;
        ctrl._rtInvalidated = true;     // 布局变化需要重新烘焙
        ctrl.canvas.Invalidate();
    }

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
            if (ctrl.ImageBytes is { Length: > 0 } bytes)
                _ = ctrl.LoadBitmapAsync(bytes);
            else
                _ = ctrl.LoadDefaultCoverAsync();
        }
        else
        {
            ctrl._loadCts?.Cancel();
            // 不再需要停止 Timer，只需停止自驱动即可
            ctrl._isFading = false;
            ctrl.canvas.Invalidate();
        }
    }

    // ── 私有字段 ──────────────────────────────────────────────────────────

    private CanvasBitmap? _currentBitmap;
    private float _currentAlpha = 0f;

    private CanvasBitmap? _incomingBitmap;
    private float _incomingAlpha = 0f;

    private bool _isFading = false;
    private const float FadeSpeed = 1.5f;

    private CanvasBitmap? _queuedBitmap;
    private readonly Queue<CanvasBitmap> _disposeQueue = new();

    private CancellationTokenSource? _loadCts;
    private bool _isResourcesCreated = false;

    // ── 自驱动帧时间 ──────────────────────────────────────────────────────
    // 用 canvas.Draw 的 Timing 代替 DispatcherTimer
    private long _lastDrawTicks = 0;

    private const float HardMaxSize = 1536f;

    // ── 去重 ──────────────────────────────────────────────────────────────
    private long _lastLength = -1;
    private int _lastHash;

    // ── 遮罩缓存 ──────────────────────────────────────────────────────────
    private CanvasRenderTarget? _maskRT;   // ← 改这里
    private (float w, float h, float radius) _maskSize;
    private bool _maskInvalidated = false;

    // ── 烘焙 RenderTarget（替换 CachedEffectChain）────────────────────────
    // 每个 bitmap 槽烘焙一张 RenderTarget，淡入淡出期间只做 DrawImage。
    // 只有 bitmap / 尺寸 / shadow / mask 变化时才重新烘焙，帧内零 GPU 对象分配。
    private CanvasRenderTarget? _currentRT;
    private CanvasRenderTarget? _incomingRT;

    // 当 layout/shadow 变化时置 true，下一帧重新烘焙
    private bool _rtInvalidated = false;

    // 上一次烘焙时用的尺寸，用来检测是否需要重建
    private (float w, float h) _lastBakeSize;

    // ── 构造函数 ──────────────────────────────────────────────────────────

    public AlbumArtControl()
    {
        InitializeComponent();
        RegisterCanvasEvents();
        Unloaded += (_, _) => Dispose(true);
    }

    // ── 去重 ──────────────────────────────────────────────────────────────

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

    public void InvalidateDedup()
    {
        _lastLength = -1;
        _lastHash = 0;
    }

    // ── Canvas 事件 ───────────────────────────────────────────────────────

    private void RegisterCanvasEvents()
    {
        canvas.CreateResources += async (s, e) =>
        {
            _isResourcesCreated = true;
            if (!IsActive) return;

            if (ImageBytes is { Length: > 0 } bytes)
                await LoadBitmapAsync(bytes);
            else
                await LoadDefaultCoverAsync();
        };

        canvas.Draw += (s, e) =>
        {
            // ── 计算帧间 delta（替换 DispatcherTimer）──────────────────
            long nowTicks = DateTime.UtcNow.Ticks;
            float delta = _lastDrawTicks == 0
                ? 0f
                : (float)((nowTicks - _lastDrawTicks) / (double)TimeSpan.TicksPerSecond);
            // 限制 delta 上限，防止后台恢复时一次跳太多
            delta = Math.Min(delta, 0.1f);
            _lastDrawTicks = nowTicks;

            // ── 推进淡入淡出状态 ───────────────────────────────────────
            if (_isFading)
                UpdateFadeState(delta);

            // ── 绘制 ──────────────────────────────────────────────────
            DrawImageLayer(e.DrawingSession);

            // ── 自驱动：动画未结束则请求下一帧 ────────────────────────
            if (_isFading || _queuedBitmap != null)
                canvas.Invalidate();
            else
                FlushDisposeQueue();
        };
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

        // 触发第一帧，后续由 Draw 事件自驱动
        _lastDrawTicks = 0; // 重置 delta，避免首帧跳变
        canvas.Invalidate();
    }

    private void StartTransition(CanvasBitmap newBitmap)
    {
        if (_incomingBitmap != null)
        {
            if (_currentBitmap != null)
                _disposeQueue.Enqueue(_currentBitmap);

            // 迁移 RT：incoming 升格为 current
            _currentRT?.Dispose();
            _currentRT = _incomingRT;
            _incomingRT = null;

            _currentBitmap = _incomingBitmap;
            _currentAlpha = _incomingAlpha;
            _incomingBitmap = null;
            _incomingAlpha = 0f;
        }

        _incomingBitmap = newBitmap;
        _incomingAlpha = 0f;
        _incomingRT = null;     // 下一次绘制前按需烘焙
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

            _currentRT?.Dispose();
            _currentRT = _incomingRT;
            _incomingRT = null;

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
            if (bmp != null
                && bmp != _currentBitmap
                && bmp != _incomingBitmap
                && bmp != _queuedBitmap)
                bmp.Dispose();
        }
    }

    // ── 加载逻辑 ──────────────────────────────────────────────────────────

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
            InvalidateDedup();
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
        catch { }
    }

    // ── 绘制 ──────────────────────────────────────────────────────────────

    private void DrawImageLayer(CanvasDrawingSession ds)
    {
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
        float w = (float)destRect.Width;
        float h = (float)destRect.Height;
        float radius = (float)CornerRadius;

        // 遮罩按需重建
        EnsureMaskRenderTarget(ds.Device, w, h, radius);

        // 尺寸变化时标记 RT 失效
        if (MathF.Abs(_lastBakeSize.w - w) > 0.5f || MathF.Abs(_lastBakeSize.h - h) > 0.5f)
            _rtInvalidated = true;

        // RT 失效时丢弃旧的，下面会按需重新烘焙
        if (_rtInvalidated)
        {
            _currentRT?.Dispose(); _currentRT = null;
            _incomingRT?.Dispose(); _incomingRT = null;
            _rtInvalidated = false;
            _lastBakeSize = (w, h);
        }

        // 按需烘焙（仅在 RT 为空时执行，帧内只运行一次 Effect 链）
        if (_currentBitmap != null && _currentRT == null)
            _currentRT = BakeRenderTarget(ds.Device, _currentBitmap, w, h);

        if (_incomingBitmap != null && _incomingRT == null)
            _incomingRT = BakeRenderTarget(ds.Device, _incomingBitmap, w, h);

        // ── 绘制：每帧只做最多两次 DrawImage，极轻量 ──────────────────
        if (_currentRT != null && _currentAlpha > 0f)
        {
            ds.DrawImage(_currentRT,
                         new Vector2((float)destRect.X, (float)destRect.Y),
                         _currentRT.Bounds,
                         _currentAlpha);
        }

        if (_incomingRT != null && _incomingAlpha > 0f)
        {
            ds.DrawImage(_incomingRT,
                         new Vector2((float)destRect.X, (float)destRect.Y),
                         _incomingRT.Bounds,
                         _incomingAlpha);
        }
    }

    // ── RenderTarget 烘焙（每张图只执行一次 Effect 链）──────────────────

    private CanvasRenderTarget BakeRenderTarget(CanvasDevice device,
                                            CanvasBitmap bitmap,
                                            float w, float h)
    {
        var rt = new CanvasRenderTarget(device, w, h, 96f);
        try
        {
            using var rtDs = rt.CreateDrawingSession();
            rtDs.Clear(Microsoft.UI.Colors.Transparent);

            using var scale = new ScaleEffect
            {
                Source = bitmap,
                Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                     h / bitmap.SizeInPixels.Height),
                InterpolationMode = CanvasImageInterpolation.HighQualityCubic
            };

            using var masked = new AlphaMaskEffect
            {
                Source = scale,
                AlphaMask = _maskRT!    // ← CanvasRenderTarget，坐标系稳定
            };

            if (IsShadowEnabled)
            {
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
                rtDs.DrawImage(composite);
            }
            else
            {
                rtDs.DrawImage(masked);
            }
        }
        catch { }

        return rt;
    }

    // ── 遮罩缓存 ──────────────────────────────────────────────────────────

    private void EnsureMaskRenderTarget(CanvasDevice device, float w, float h, float radius)
    {
        if (!_maskInvalidated
            && _maskRT != null
            && MathF.Abs(_maskSize.w - w) < 0.5f
            && MathF.Abs(_maskSize.h - h) < 0.5f
            && MathF.Abs(_maskSize.radius - radius) < 0.5f)
        {
            return;
        }

        _maskRT?.Dispose();
        _maskRT = new CanvasRenderTarget(device, w, h, 96f);
        using (var maskDs = _maskRT.CreateDrawingSession())
        {
            maskDs.Clear(Microsoft.UI.Colors.Transparent);
            maskDs.FillRoundedRectangle(0, 0, w, h, radius, radius,
                                        Microsoft.UI.Colors.White);
        }

        _maskSize = (w, h, radius);
        _maskInvalidated = false;

        _currentRT?.Dispose(); _currentRT = null;
        _incomingRT?.Dispose(); _incomingRT = null;
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

        return new(cx + (cw - drawW) * 0.5f,
                   cy + (ch - drawH) * 0.5f,
                   drawW, drawH);
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

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _maskRT?.Dispose(); _maskRT = null;

        _currentRT?.Dispose(); _currentRT = null;
        _incomingRT?.Dispose(); _incomingRT = null;

        _currentBitmap?.Dispose(); _currentBitmap = null;
        _incomingBitmap?.Dispose(); _incomingBitmap = null;
        _queuedBitmap?.Dispose(); _queuedBitmap = null;

        while (_disposeQueue.TryDequeue(out var b))
            b.Dispose();
    }
}