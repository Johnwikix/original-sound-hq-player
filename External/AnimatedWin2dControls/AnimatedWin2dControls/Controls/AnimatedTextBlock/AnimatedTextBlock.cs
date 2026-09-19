using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.Text;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock;

[TemplatePart(Name = "ContentBorder", Type = typeof(Border))]
[TemplatePart(Name = "AnimatedCanvas", Type = typeof(CanvasControl))]
public sealed partial class AnimatedTextBlock : Control, ISharedTickable
{
    // ── 替换为 CanvasControl ──────────────────────────────────────────────
    private CanvasControl _canvas = null;

    // ── 文字状态 ─────────────────────────────────────────────────────────
    private string _oldText = string.Empty;
    private string _newText = string.Empty;

    // ── 动画驱动 ─────────────────────────────────────────────────────────
    private AnimatedTextBlockRedrawState _currentState = AnimatedTextBlockRedrawState.Idle;
    //private bool _isRenderingHooked = false;
    //private DateTimeOffset _lastRenderTime;
    private string _cachedLayoutText = null;
    private float _cachedLayoutWidth, _cachedLayoutHeight;
    private int _cachedFormatVersion;
    private int _formatVersion;
    private bool _isClockRegistered = false;
    private TimeSpan _totalAnimationTime;       // 替代 CanvasTimingInformation.TotalTime
    private TimeSpan _animationBeginTime;

    // ── Diff / Layout ─────────────────────────────────────────────────────
    private List<TextDiffResult> _diffResults = null;

    private CanvasTextFormat _textFormat = new CanvasTextFormat();
    private CanvasLinearGradientBrush _textBrush;
    private Color _textColor = Colors.Black;

    private CanvasTextLayout _oldTextLayout;
    private CanvasTextLayout _newTextLayout;
    private CanvasTextLayout _staticTextLayout;
    private bool _staticLayoutDirty = true;

    // ── 文字效果 ──────────────────────────────────────────────────────────
    private ITextEffect _textEffect;

    // ── 字体属性缓存 ──────────────────────────────────────────────────────
    private float _fontSize = 14;
    private string _fontFamily = FontFamily.XamlAutoFontFamily.Source;
    private FontStretch _fontStretch = FontStretch.Normal;
    private FontStyle _fontStyle = FontStyle.Normal;
    private FontWeight _fontWeight = FontWeights.Normal;
    private bool _textFormatDirty = true;

    // ── 文字布局属性缓存 ──────────────────────────────────────────────────
    private TextAlignment _textAlignment = TextAlignment.Left;
    private AnimatedTextBlockTextDirection _textDirection = AnimatedTextBlockTextDirection.LeftToRightThenTopToBottom;
    private TextTrimming _textTrimming = TextTrimming.None;
    private TextWrapping _textWrapping = TextWrapping.NoWrap;

    #region DependencyProperties

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(AnimatedTextBlock), new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty TextEffectProperty = DependencyProperty.Register(
        nameof(TextEffect), typeof(ITextEffect), typeof(AnimatedTextBlock), new PropertyMetadata(null, OnTextEffectChanged));

    public ITextEffect TextEffect
    {
        get => (ITextEffect)GetValue(TextEffectProperty);
        set => SetValue(TextEffectProperty, value);
    }

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(AnimatedTextBlock), new PropertyMetadata(TextAlignment.Left, OnTextLayoutPropertyChanged));

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public static readonly DependencyProperty TextDirectionProperty = DependencyProperty.Register(
        nameof(TextDirection), typeof(AnimatedTextBlockTextDirection), typeof(AnimatedTextBlock), new PropertyMetadata(AnimatedTextBlockTextDirection.LeftToRightThenTopToBottom, OnTextLayoutPropertyChanged));

    public AnimatedTextBlockTextDirection TextDirection
    {
        get => (AnimatedTextBlockTextDirection)GetValue(TextDirectionProperty);
        set => SetValue(TextDirectionProperty, value);
    }

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming), typeof(TextTrimming), typeof(AnimatedTextBlock), new PropertyMetadata(TextTrimming.None, OnTextLayoutPropertyChanged));

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(AnimatedTextBlock), new PropertyMetadata(TextWrapping.NoWrap, OnTextLayoutPropertyChanged));

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight), typeof(double), typeof(AnimatedTextBlock), new PropertyMetadata(0d, OnTextLayoutPropertyChanged));

    /// <summary>Uniform line height in DIPs; zero uses the font's natural line metrics.</summary>
    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public static readonly DependencyProperty IsHoverScrollEnabledProperty = DependencyProperty.Register(
        nameof(IsHoverScrollEnabled), typeof(bool), typeof(AnimatedTextBlock), new PropertyMetadata(false, OnHoverScrollEnabledChanged));

    /// <summary>
    /// Scroll actually trimmed horizontal lines while the mouse is over
    /// the control. Text transitions finish before scrolling starts. Defaults to false.
    /// </summary>
    public bool IsHoverScrollEnabled
    {
        get => (bool)GetValue(IsHoverScrollEnabledProperty);
        set => SetValue(IsHoverScrollEnabledProperty, value);
    }

    public bool IsAnimating => _currentState != AnimatedTextBlockRedrawState.Idle;

    #endregion

    public event EventHandler<AnimatedTextBlockRedrawState> RedrawStateChanged;

    public AnimatedTextBlock()
    {
        this.DefaultStyleKey = typeof(AnimatedTextBlock);

        this.Loaded += OnLoaded;
        this.Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerCanceled += OnPointerExited;
        RegisterPropertyChangedCallback(PaddingProperty, OnTextMetricsChanged);
        RegisterPropertyChangedCallback(BorderThicknessProperty, OnTextMetricsChanged);
        this.RegisterPropertyChangedCallback(ForegroundProperty, ForegroundChangedCallback);
        this.RegisterPropertyChangedCallback(FontFamilyProperty, FontFamilyChangedCallback);
        this.RegisterPropertyChangedCallback(FontSizeProperty, FontSizeChangedCallback);
        this.RegisterPropertyChangedCallback(FontStretchProperty, FontStretchChangedCallback);
        this.RegisterPropertyChangedCallback(FontStyleProperty, FontStyleChangedCallback);
        this.RegisterPropertyChangedCallback(FontWeightProperty, FontWeightChangedCallback);

        _textFormat.TrimmingSign = CanvasTrimmingSign.Ellipsis;
    }

    protected override void OnApplyTemplate()
    {
        StopHoverScroll();
        StopRenderingLoop();
        DetachCanvas();
        DisposeLayouts();
        base.OnApplyTemplate();

        _canvas = GetTemplateChild("AnimatedCanvas") as CanvasControl;

        ApplyTextFormatIfNeeded();
        ApplyTextForeground();

        AttachCanvas();
        if (IsLoaded)
            SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureTextFormat();
        AttachCanvas();
        ApplyTextFormatIfNeeded();
        ApplyTextForeground();
        _newText = Text ?? string.Empty;
        _staticLayoutDirty = true;
        InvalidateMeasure();
        SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isPointerOver = false;
        StopHoverScroll();
        StopRenderingLoop();
        _currentState = AnimatedTextBlockRedrawState.Idle;
        DetachCanvas();

        // 释放所有 GPU 资源
        DisposeLayouts();
        _textBrush?.Dispose();
        _textBrush = null;
        _textFormat?.Dispose();
        _textFormat = null;
        _textFormatDirty = true;
        _diffResults = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        StopHoverScroll();
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            SetRedrawState(AnimatedTextBlockRedrawState.Idle);
            return;
        }

        _staticLayoutDirty = true;

        // A font fallback or line-count change can resize an Auto row during a
        // text transition. Reinitialize both layouts at the new size on Draw;
        // entering Idle here would silently discard the requested transition.
        if (_textEffect != null && IsAnimating)
        {
            SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
            return;
        }

        if (_canvas != null && _canvas.Size.Width > 0 && _canvas.Size.Height > 0)
        {
            ApplyTextFormatIfNeeded();
            RebuildNewTextLayout(_canvas);
        }

        SetRedrawState(AnimatedTextBlockRedrawState.Idle);
    }

    #region Property Changed Callbacks

    private void ForegroundChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        ApplyTextForeground();
        _staticLayoutDirty = true;
        _canvas?.Invalidate();
    }

    private void FontFamilyChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontFamily = FontFamily.Source;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        StopHoverScroll();
        InvalidateMeasure();
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private void FontSizeChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontSize = (float)FontSize;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        StopHoverScroll();
        InvalidateMeasure();
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private void FontStretchChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontStretch = FontStretch;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        StopHoverScroll();
        InvalidateMeasure();
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private void FontStyleChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontStyle = FontStyle;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        StopHoverScroll();
        InvalidateMeasure();
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private void FontWeightChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontWeight = FontWeight;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        StopHoverScroll();
        InvalidateMeasure();
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    #endregion

    #region Canvas Events

    private void Canvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        DisposeLayouts();
        StopHoverScroll();
        _staticLayoutDirty = true;
        if (_currentState == AnimatedTextBlockRedrawState.Animating)
            SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
        if (Foreground is LinearGradientBrush linearGradientBrush)
        {
            var stops = new CanvasGradientStop[linearGradientBrush.GradientStops.Count];

            for (int i = 0; i < linearGradientBrush.GradientStops.Count; i++)
            {
                stops[i].Color = linearGradientBrush.GradientStops[i].Color;
                stops[i].Position = (float)linearGradientBrush.GradientStops[i].Offset;
            }

            _textBrush?.Dispose();
            _textBrush = new CanvasLinearGradientBrush(sender, stops);
        }
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        // 全部在 UI 线程，无需 lock
        args.DrawingSession.Clear(Colors.Transparent);

        if (sender.Size.Width <= 0 || sender.Size.Height <= 0)
            return;

        ApplyTextFormatIfNeeded();

        // ── 无动画效果 or Idle：画静态帧 ────────────────────────────────
        if (_textEffect == null || _currentState == AnimatedTextBlockRedrawState.Idle)
        {
            if (_currentState != AnimatedTextBlockRedrawState.Idle)
                SetRedrawState(AnimatedTextBlockRedrawState.Idle, false);
            DrawStatic(sender, args.DrawingSession);
            return;
        }

        // ── TextChanged 初始化：在 Draw 里完成 layout 构建 ───────────────
        // （避免在 UI 线程以外操作 CanvasDevice）
        if (_currentState == AnimatedTextBlockRedrawState.TextChanged)
        {
            ApplyTextFormatIfNeeded();
            RebuildOldTextLayout(sender);
            RebuildNewTextLayout(sender);

            if (_textEffect is TextFadeEffect fadeEffect)
            {
                fadeEffect.Reset();
            }
            else if (_textEffect is TextWipeEffect textWipeEffect)
            {
                textWipeEffect.Reset();
            }
            else
            {
                GenerateDiffResults();
                _animationBeginTime = _totalAnimationTime;
            }

            _currentState = AnimatedTextBlockRedrawState.Animating;
        }

        // ── Animating：交给 TextEffect 绘制 ─────────────────────────────
        if (_currentState == AnimatedTextBlockRedrawState.Animating)
        {
            if (_newTextLayout == null) return;

            try
            {
                bool isScanEffect = _textEffect is TextFadeEffect || _textEffect is TextWipeEffect;
                _textEffect.DrawText(
                     _oldText, _newText,
                     isScanEffect ? null : _diffResults,
                     _oldTextLayout, _newTextLayout,
                     _textFormat, _textColor, _textBrush,
                     _currentState,
                     args.DrawingSession);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is ArgumentException) { }
        }
    }

    #endregion

    #region Rendering Loop (SharedAnimationClock)

    private void StartRenderingLoop()
    {
        if (_isClockRegistered || !IsLoaded) return;
        SharedAnimationClock.Register(this);
        _isClockRegistered = true;
    }

    private void StopRenderingLoop()
    {
        if (!_isClockRegistered) return;
        SharedAnimationClock.Unregister(this);
        _isClockRegistered = false;
    }


    #endregion

    #region Text Format & Foreground

    private void ApplyTextFormatIfNeeded()
    {
        EnsureTextFormat();
        if (!_textFormatDirty) return;

        _textFormat.FontSize = _fontSize;
        _textFormat.FontFamily = _fontFamily;
        _textFormat.FontStretch = _fontStretch;
        _textFormat.FontStyle = _fontStyle;
        _textFormat.FontWeight = _fontWeight;
        _textFormat.Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap;
        _textFormat.HorizontalAlignment = Win2dHelpers.MapCanvasHorizontalAlignment(_textAlignment);
        _textFormat.VerticalAlignment = CanvasVerticalAlignment.Center;
        _textFormat.Direction = Win2dHelpers.MapTextDirection(_textDirection);
        _textFormat.TrimmingGranularity = Win2dHelpers.MapTrimmingGranularity(_textTrimming);
        _textFormat.WordWrapping = Win2dHelpers.MapWordWrapping(_textWrapping);
        ApplyLineSpacing();

        _textFormatDirty = false;
        _formatVersion++;
    }

    private void ApplyTextForeground()
    {
        if (Foreground is SolidColorBrush colorBrush)
        {
            _textColor = colorBrush.Color;
            _textBrush?.Dispose();
            _textBrush = null;
        }
        else if (Foreground is LinearGradientBrush linearGradientBrush)
        {
            if (_canvas != null)
            {
                var stops = new CanvasGradientStop[linearGradientBrush.GradientStops.Count];

                for (int i = 0; i < linearGradientBrush.GradientStops.Count; i++)
                {
                    stops[i] = new CanvasGradientStop()
                    {
                        Color = linearGradientBrush.GradientStops[i].Color,
                        Position = (float)linearGradientBrush.GradientStops[i].Offset
                    };
                }

                _textBrush?.Dispose();
                _textBrush = new CanvasLinearGradientBrush(_canvas, stops);
            }
        }
        else
        {
            if (Application.Current.Resources["TextFillColorPrimaryBrush"] is SolidColorBrush defaultBrush)
            {
                _textColor = defaultBrush.Color;
                _textBrush?.Dispose();
                _textBrush = null;
            }
        }
    }

    #endregion

    #region Layout Helpers

    /// <summary>
    /// 画静态帧（Idle 状态或无动画效果时）。
    /// 复用缓存的 _staticTextLayout，只在 dirty 时重建。
    /// </summary>
    private void DrawStatic(CanvasControl sender, CanvasDrawingSession ds)
    {
        if (_staticLayoutDirty || _staticTextLayout == null)
        {
            _staticTextLayout?.Dispose();
            _staticTextLayout = new CanvasTextLayout(sender,
                _newText, _textFormat,
                (float)sender.Size.Width,
                (float)sender.Size.Height);
            _staticTextLayout.Options = CanvasDrawTextOptions.EnableColorFont;
            _staticLayoutDirty = false;
        }

        if (_staticTextLayout == null) return;

        EnsureHoverScroll(sender);
        try
        {
            if (_hoverLines == null)
            {
                DrawTextLayout(ds, _staticTextLayout, 0, 0);
            }
            else
            {
                foreach (var line in _hoverLines)
                    DrawTextLayout(ds, line.Layout, GetHoverOffset(line.Distance), line.Y);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is ArgumentException) { }
    }

    private void DrawTextLayout(CanvasDrawingSession ds, CanvasTextLayout layout, float x, float y)
    {
        if (_textBrush != null)
            ds.DrawTextLayout(layout, x, y, _textBrush);
        else
            ds.DrawTextLayout(layout, x, y, _textColor);
    }

    private void RebuildOldTextLayout(CanvasControl resourceCreator)
    {
        _oldTextLayout?.Dispose();
        _oldTextLayout = new CanvasTextLayout(resourceCreator, _oldText, _textFormat,
            (float)resourceCreator.Size.Width,
            (float)resourceCreator.Size.Height);
        _oldTextLayout.Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap;
        _oldTextLayout.VerticalAlignment = CanvasVerticalAlignment.Center;
    }

    private void RebuildNewTextLayout(CanvasControl resourceCreator)
    {
        if (resourceCreator == null || !resourceCreator.ReadyToDraw)
            return;

        float w = (float)resourceCreator.Size.Width;
        float h = (float)resourceCreator.Size.Height;

        // 文字、尺寸、格式版本都没变则跳过重建
        if (_newTextLayout != null
            && _cachedLayoutText == _newText
            && _cachedFormatVersion == _formatVersion
            && Math.Abs(_cachedLayoutWidth - w) < 0.5f
            && Math.Abs(_cachedLayoutHeight - h) < 0.5f)
            return;

        _newTextLayout?.Dispose();
        _newTextLayout = new CanvasTextLayout(resourceCreator, _newText, _textFormat, w, h);
        _newTextLayout.Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap;
        _newTextLayout.VerticalAlignment = CanvasVerticalAlignment.Center;

        _cachedLayoutText = _newText;
        _cachedFormatVersion = _formatVersion;
        _cachedLayoutWidth = w;
        _cachedLayoutHeight = h;
        _staticLayoutDirty = true;
    }

    private void GenerateDiffResults()
    {
        var oldClusters = TextRenderingHelper.GenerateGraphemeClusters(_oldText, _oldTextLayout);
        var newClusters = TextRenderingHelper.GenerateGraphemeClusters(_newText, _newTextLayout);
        _diffResults = GraphemeClusterDiff.Diff(oldClusters, newClusters);
    }

    private void DisposeLayouts()
    {
        _oldTextLayout?.Dispose();
        _oldTextLayout = null;
        _newTextLayout?.Dispose();
        _newTextLayout = null;
        _staticTextLayout?.Dispose();
        _staticTextLayout = null;
    }

    #endregion

    #region Animation Progress

    private void UpdateAllClusterProgress(TimeSpan elapsed)
    {
        if (_diffResults == null) return;

        var animationDuration = _textEffect?.AnimationDuration ?? TimeSpan.FromMilliseconds(600);
        var delayPerCluster = _textEffect?.DelayPerCluster ?? TimeSpan.Zero;

        // step = elapsed / animationDuration，表示本帧推进多少进度
        float step = (float)(elapsed.TotalMilliseconds / animationDuration.TotalMilliseconds);

        var delay = delayPerCluster <= animationDuration ? delayPerCluster : animationDuration;

        int insertDelayOffset = 0;
        int moveDelayOffset = 0;
        int removeDelayOffset = 0;
        int updateDelayOffset = 0;

        int ongoingAnimations = 0;

        for (int i = 0; i < _diffResults.Count; i++)
        {
            var diffResult = _diffResults[i];

            int delayOffset;

            switch (diffResult.Type)
            {
                case AnimatedTextBlockDiffOperationType.Move:
                    delayOffset = moveDelayOffset++;
                    break;
                case AnimatedTextBlockDiffOperationType.Insert:
                    delayOffset = insertDelayOffset++;
                    break;
                case AnimatedTextBlockDiffOperationType.Remove:
                    delayOffset = removeDelayOffset++;
                    break;
                case AnimatedTextBlockDiffOperationType.Update:
                    delayOffset = updateDelayOffset++;
                    break;
                default:
                    delayOffset = moveDelayOffset++;
                    break;
            }

            if (!UpdateClusterProgress(diffResult.OldGlyphCluster, delayOffset, step, delay))
                ongoingAnimations++;

            if (!UpdateClusterProgress(diffResult.NewGlyphCluster, delayOffset, step, delay))
                ongoingAnimations++;
        }

        if (ongoingAnimations < 1)
        {
            SetRedrawState(AnimatedTextBlockRedrawState.Idle);
        }
    }

    /// <summary>
    /// 更新单个 cluster 的进度。
    /// </summary>
    /// <returns>true 表示该 cluster 动画已完成</returns>
    private bool UpdateClusterProgress(GraphemeCluster cluster, int offset, float step, TimeSpan delay)
    {
        if (cluster == null) return true;

        var duration = _textEffect?.AnimationDuration ?? TimeSpan.Zero;

        // 从动画开始算起，该 cluster 的结束时间点
        bool isFinished = _totalAnimationTime.TotalMilliseconds >=
                          (_animationBeginTime.TotalMilliseconds +
                           delay.TotalMilliseconds * offset +
                           duration.TotalMilliseconds);

        if (isFinished)
        {
            cluster.Progress = 1.0f;
            cluster.IsAnimationFinished = true;
            return true;
        }

        // 还在延迟等待阶段，进度保持 0
        bool inDelay = _totalAnimationTime.TotalMilliseconds - _animationBeginTime.TotalMilliseconds
                       < delay.TotalMilliseconds * offset;

        if (inDelay)
        {
            cluster.Progress = 0;
            return false;
        }

        cluster.Progress = Math.Clamp(cluster.Progress + step, 0f, 1.0f);
        return false;
    }

    private void ResetAllClusterProgress()
    {
        if (_diffResults == null) return;

        foreach (var diffResult in _diffResults)
        {
            if (diffResult.OldGlyphCluster != null)
            {
                diffResult.OldGlyphCluster.Progress = 0;
                diffResult.OldGlyphCluster.IsAnimationFinished = false;
            }
            if (diffResult.NewGlyphCluster != null)
            {
                diffResult.NewGlyphCluster.Progress = 0;
                diffResult.NewGlyphCluster.IsAnimationFinished = false;
            }
        }
    }

    private void RebuildLayoutsIfReady()
    {
        if (_canvas == null || _canvas.Size.Width <= 0 || _canvas.Size.Height <= 0)
            return;

        RebuildNewTextLayout(_canvas);
    }
    #endregion
    public void OnSharedTick(TimeSpan elapsed)
    {
        // 空闲状态仅在悬停滚动期间重绘。
        if (_currentState == AnimatedTextBlockRedrawState.Idle)
        {
            if (_hoverLines != null)
            {
                _hoverElapsed += elapsed.TotalSeconds;
                _canvas?.Invalidate();
            }
            return;
        }

        _totalAnimationTime += elapsed;

        if (_currentState == AnimatedTextBlockRedrawState.Animating)
        {
            if (_textEffect is TextFadeEffect fadeEffect)
            {
                fadeEffect.Advance(elapsed);
                if (fadeEffect.IsFinished)
                    SetRedrawState(AnimatedTextBlockRedrawState.Idle);
            }
            else if (_textEffect is TextWipeEffect wipeEffect)
            {
                wipeEffect.Advance(elapsed);
                if (wipeEffect.IsFinished)
                    SetRedrawState(AnimatedTextBlockRedrawState.Idle);
            }
            else
            {
                UpdateAllClusterProgress(elapsed);
            }
        }

        // 只有真正需要重绘时才 Invalidate
        _canvas?.Invalidate();
    }
    private void SetRedrawState(AnimatedTextBlockRedrawState state, bool fireEvent = true)
    {
        if (state != AnimatedTextBlockRedrawState.Idle)
            StopHoverScroll();
        _currentState = state;

        switch (state)
        {
            case AnimatedTextBlockRedrawState.Animating:
                StartRenderingLoop();
                break;

            case AnimatedTextBlockRedrawState.TextChanged:
                if (_textEffect != null)
                    StartRenderingLoop();
                _canvas?.Invalidate(); // 触发一次 Draw 完成初始化
                break;

            case AnimatedTextBlockRedrawState.Idle:
                StopRenderingLoop();          // 从共享时钟注销，不再收到 tick
                _canvas?.Invalidate();        // 最后一帧刷新为最终状态
                break;

            case AnimatedTextBlockRedrawState.LayoutChanged:
                StopRenderingLoop();
                _canvas?.Invalidate();
                break;
        }

        if (fireEvent)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal,
                () => RedrawStateChanged?.Invoke(this, _currentState));
        }
    }
}
