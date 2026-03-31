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
        if (ctrl.IsDuplicateAndUpdate(newBytes)) return;    // ← 改为实例方法

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

        ctrl.InvalidateDedup();                             // ← 改为实例方法

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
            typeof(AlbumArtControl), new PropertyMetadata(true, OnShadowEnabledChanged));  // ← 独立回调
    public bool IsShadowEnabled
    {
        get => (bool)GetValue(IsShadowEnabledProperty);
        set => SetValue(IsShadowEnabledProperty, value);
    }

    // IsShadowEnabled 变化时重建缓存 Effect 链
    private static void OnShadowEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (AlbumArtControl)d;
        if (!ctrl._isResourcesCreated) return;
        ctrl._effectChainInvalidated = true;
        ctrl.canvas.Invalidate();
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (AlbumArtControl)d;
        if (!ctrl._isResourcesCreated) return;
        if (!ctrl.IsActive) return;

        ctrl._maskInvalidated = true;
        ctrl._effectChainInvalidated = true;               // ← 布局变化也要重建
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

    // ── 去重：改为实例字段，消除多实例互相干扰 ─────────────────────────
    private long _lastLength = -1;
    private int _lastHash;

    // ── 缓存的圆角遮罩 ────────────────────────────────────────────────────
    private CanvasCommandList? _maskCL;
    private (float w, float h, float radius) _maskSize;
    private bool _maskInvalidated = false;

    // ── 缓存的 Effect 链（核心修复：不再每帧 new）────────────────────────
    //
    // 每个可见 bitmap 槽各持有一条 Effect 链：
    //   ScaleEffect → AlphaMaskEffect → (ShadowEffect + Transform2DEffect) → CompositeEffect → OpacityEffect
    // 只有当 bitmap、尺寸或 IsShadowEnabled 变化时才重建。
    // 帧内仅更新 OpacityEffect.Opacity 与 ScaleEffect.Scale，无 GPU 对象分配。

    private CachedEffectChain? _currentChain;
    private CachedEffectChain? _incomingChain;

    private bool _effectChainInvalidated = false;

    // ── 构造函数 ──────────────────────────────────────────────────────────

    public AlbumArtControl()
    {
        InitializeComponent();
        RegisterCanvasEvents();
        Unloaded += (_, _) => Dispose(true);
    }

    // ── 去重（实例方法）───────────────────────────────────────────────────

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
            // 把 incoming 升格为 current，同时迁移其 Effect 链
            if (_currentBitmap != null)
                _disposeQueue.Enqueue(_currentBitmap);

            _currentChain?.Dispose();
            _currentChain = _incomingChain;
            _incomingChain = null;

            _currentBitmap = _incomingBitmap;
            _currentAlpha = _incomingAlpha;
            _incomingBitmap = null;
            _incomingAlpha = 0f;
        }

        _incomingBitmap = newBitmap;
        _incomingAlpha = 0f;
        _incomingChain = null;                              // 下一帧绘制时按需建链
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

            _currentChain?.Dispose();
            _currentChain = _incomingChain;
            _incomingChain = null;

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
        float radius = (float)CornerRadius;
        float w = (float)destRect.Width;
        float h = (float)destRect.Height;

        EnsureMaskCommandList(ds.Device, w, h, radius);

        // 重建 Effect 链（仅在必要时）
        if (_effectChainInvalidated)
        {
            _currentChain?.InvalidateLayout();
            _incomingChain?.InvalidateLayout();
            _effectChainInvalidated = false;
        }

        if (_currentBitmap != null && _currentAlpha > 0f)
        {
            EnsureChain(ds.Device, ref _currentChain, _currentBitmap, w, h);
            TryDrawChain(ds, _currentChain!, destRect, _currentAlpha);
        }

        if (_incomingBitmap != null && _incomingAlpha > 0f)
        {
            EnsureChain(ds.Device, ref _incomingChain, _incomingBitmap, w, h);
            TryDrawChain(ds, _incomingChain!, destRect, _incomingAlpha);
        }
    }

    // ── Effect 链缓存管理 ─────────────────────────────────────────────────

    private void EnsureChain(CanvasDevice device, ref CachedEffectChain? chain,
                             CanvasBitmap bitmap, float w, float h)
    {
        if (chain == null || chain.NeedsRebuild(bitmap, w, h))
        {
            chain?.Dispose();
            chain = new CachedEffectChain(bitmap, _maskCL!, w, h, IsShadowEnabled);
        }
    }

    private static void TryDrawChain(CanvasDrawingSession ds,
                                     CachedEffectChain chain,
                                     Windows.Foundation.Rect dest,
                                     float opacity)
    {
        try
        {
            chain.Opacity = opacity;
            ds.DrawImage(chain.Root, (float)dest.X, (float)dest.Y);
        }
        catch { /* device lost，忽略本帧 */ }
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

        // 遮罩重建后 Effect 链也必须重建（AlphaMask 引用了旧的 _maskCL）
        _currentChain?.Dispose(); _currentChain = null;
        _incomingChain?.Dispose(); _incomingChain = null;
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

        _currentChain?.Dispose(); _currentChain = null;
        _incomingChain?.Dispose(); _incomingChain = null;

        _currentBitmap?.Dispose(); _currentBitmap = null;
        _incomingBitmap?.Dispose(); _incomingBitmap = null;
        _queuedBitmap?.Dispose(); _queuedBitmap = null;

        while (_disposeQueue.TryDequeue(out var b))
            b.Dispose();
    }
}

// ── 缓存的 Effect 链（每个 bitmap 槽一个实例）────────────────────────────
//
// 持有整条 ScaleEffect → AlphaMaskEffect → [Shadow →] CompositeEffect → OpacityEffect
// 帧内只更新 Opacity 和 Scale，不分配任何新对象。

internal sealed class CachedEffectChain : IDisposable
{
    private readonly ScaleEffect _scale;
    private readonly AlphaMaskEffect _masked;
    private readonly ShadowEffect? _shadow;
    private readonly Transform2DEffect? _shadowOffset;
    private readonly CompositeEffect _composite;
    private readonly OpacityEffect _withOpacity;

    private readonly CanvasBitmap _bitmap;
    private readonly float _w;
    private readonly float _h;
    private bool _layoutValid = true;

    public ICanvasImage Root => _withOpacity;

    public float Opacity
    {
        get => _withOpacity.Opacity;
        set => _withOpacity.Opacity = value;
    }

    public CachedEffectChain(CanvasBitmap bitmap,
                              CanvasCommandList maskCL,
                              float w, float h,
                              bool shadowEnabled)
    {
        _bitmap = bitmap;
        _w = w;
        _h = h;

        _scale = new ScaleEffect
        {
            Source = bitmap,
            Scale = new Vector2(w / bitmap.SizeInPixels.Width,
                                 h / bitmap.SizeInPixels.Height),
            InterpolationMode = CanvasImageInterpolation.HighQualityCubic
        };

        _masked = new AlphaMaskEffect { Source = _scale, AlphaMask = maskCL };

        _composite = new CompositeEffect();

        if (shadowEnabled)
        {
            _shadow = new ShadowEffect
            {
                Source = _masked,
                BlurAmount = 10f,
                ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0)
            };
            _shadowOffset = new Transform2DEffect
            {
                Source = _shadow,
                TransformMatrix = Matrix3x2.CreateTranslation(2f, 3f)
            };
            _composite.Sources.Add(_shadowOffset);
        }

        _composite.Sources.Add(_masked);

        _withOpacity = new OpacityEffect { Source = _composite, Opacity = 1f };
    }

    /// <summary>尺寸或 bitmap 变化时需要重建。</summary>
    public bool NeedsRebuild(CanvasBitmap bitmap, float w, float h)
        => !_layoutValid
           || !ReferenceEquals(bitmap, _bitmap)
           || MathF.Abs(w - _w) > 0.5f
           || MathF.Abs(h - _h) > 0.5f;

    /// <summary>布局属性变化（IsShadowEnabled / CornerRadius 等）时标记重建。</summary>
    public void InvalidateLayout() => _layoutValid = false;

    public void Dispose()
    {
        _withOpacity.Dispose();
        _composite.Dispose();
        _shadowOffset?.Dispose();
        _shadow?.Dispose();
        _masked.Dispose();
        _scale.Dispose();
    }
}