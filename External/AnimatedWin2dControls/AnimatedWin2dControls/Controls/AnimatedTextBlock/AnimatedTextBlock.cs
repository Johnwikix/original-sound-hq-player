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
using System.Numerics;
using Windows.UI;
using Windows.UI.Text;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock;

[TemplatePart(Name = "ContentBorder", Type = typeof(Border))]
[TemplatePart(Name = "AnimatedCanvas", Type = typeof(CanvasControl))]
public sealed partial class AnimatedTextBlock : Control
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
        nameof(Text), typeof(string), typeof(AnimatedTextBlock), new PropertyMetadata(default(string)));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set
        {
            _oldText = _newText ?? string.Empty;
            _newText = value ?? string.Empty;
            SetValue(TextProperty, value);
            SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
        }
    }

    public static readonly DependencyProperty TextEffectProperty = DependencyProperty.Register(
        nameof(TextEffect), typeof(ITextEffect), typeof(AnimatedTextBlock), new PropertyMetadata(default(ITextEffect)));

    public ITextEffect TextEffect
    {
        get => (ITextEffect)GetValue(TextEffectProperty);
        set
        {
            _textEffect = value;
            SetValue(TextEffectProperty, value);
        }
    }

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(AnimatedTextBlock), new PropertyMetadata(default(TextAlignment)));

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set
        {
            _textAlignment = value;
            _textFormatDirty = true;
            SetValue(TextAlignmentProperty, value);
        }
    }

    public static readonly DependencyProperty TextDirectionProperty = DependencyProperty.Register(
        nameof(TextDirection), typeof(AnimatedTextBlockTextDirection), typeof(AnimatedTextBlock), new PropertyMetadata(default(AnimatedTextBlockTextDirection)));

    public AnimatedTextBlockTextDirection TextDirection
    {
        get => (AnimatedTextBlockTextDirection)GetValue(TextDirectionProperty);
        set
        {
            _textDirection = value;
            _textFormatDirty = true;
            SetValue(TextDirectionProperty, value);
        }
    }

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming), typeof(TextTrimming), typeof(AnimatedTextBlock), new PropertyMetadata(default(TextTrimming)));

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set
        {
            _textTrimming = value;
            _textFormatDirty = true;
            SetValue(TextTrimmingProperty, value);
        }
    }

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(AnimatedTextBlock), new PropertyMetadata(default(TextWrapping)));

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set
        {
            _textWrapping = value;
            _textFormatDirty = true;
            SetValue(TextWrappingProperty, value);
        }
    }

    public bool IsAnimating => _currentState != AnimatedTextBlockRedrawState.Idle;

    #endregion

    public event EventHandler<AnimatedTextBlockRedrawState> RedrawStateChanged;

    public AnimatedTextBlock()
    {
        this.DefaultStyleKey = typeof(AnimatedTextBlock);

        this.Loaded += OnLoaded;
        this.Unloaded += OnUnloaded;
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
        base.OnApplyTemplate();

        _canvas = GetTemplateChild("AnimatedCanvas") as CanvasControl;
        this.SizeChanged += OnSizeChanged;

        ApplyTextFormatIfNeeded();
        ApplyTextForeground();

        if (_canvas != null)
        {
            _canvas.CreateResources += Canvas_CreateResources;
            _canvas.Draw += Canvas_Draw;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _newText = Text ?? string.Empty;
        SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 停止渲染循环
        StopRenderingLoop();

        // 解绑画布事件
        if (_canvas != null)
        {
            _canvas.CreateResources -= Canvas_CreateResources;
            _canvas.Draw -= Canvas_Draw;
        }

        // 释放所有 GPU 资源
        DisposeLayouts();
        _textBrush?.Dispose();
        _textBrush = null;
        _textFormat?.Dispose();
        _textFormat = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            return;

        // 尺寸变化：重建 newTextLayout 后直接进 Idle，不跑动画
        // 在 UI 线程操作，此时没有并发问题
        _staticLayoutDirty = true;

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
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private void FontSizeChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontSize = (float)FontSize;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private void FontStretchChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontStretch = FontStretch;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private void FontStyleChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontStyle = FontStyle;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private void FontWeightChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontWeight = FontWeight;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    #endregion

    #region Canvas Events

    private void Canvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
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

        // ── 无动画效果 or Idle：画静态帧 ────────────────────────────────
        if (_textEffect == null || _currentState == AnimatedTextBlockRedrawState.Idle)
        {
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
            } else if (_textEffect is TextWipeEffect textWipeEffect) {
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
        if (_isClockRegistered) return;
        SharedAnimationClock.Register(this);
        _isClockRegistered = true;
    }

    private void StopRenderingLoop()
    {
        if (!_isClockRegistered) return;
        SharedAnimationClock.Unregister(this);
        _isClockRegistered = false;
    }


    //private void OnRendering(object sender, object e)
    //{
    //    var now = DateTimeOffset.Now;
    //    var elapsed = now - _lastRenderTime;
    //    _lastRenderTime = now;

    //    // 累加总时间（模拟 CanvasTimingInformation.TotalTime）
    //    _totalAnimationTime += elapsed;

    //    if (_currentState == AnimatedTextBlockRedrawState.Animating)
    //    {
    //        if (_textEffect is TextFadeEffect fadeEffect)
    //        {
    //            fadeEffect.Advance(elapsed);
    //            if (fadeEffect.IsFinished)
    //                SetRedrawState(AnimatedTextBlockRedrawState.Idle);
    //        }
    //        else if (_textEffect is TextWipeEffect textWipeEffect) // 补上这个判断
    //        {
    //            textWipeEffect.Advance(elapsed);
    //            if (textWipeEffect.IsFinished) SetRedrawState(AnimatedTextBlockRedrawState.Idle);
    //        }
    //        else
    //        {
    //            UpdateAllClusterProgress(elapsed);
    //        }
    //    }

    //    _canvas?.Invalidate();
    //}

    #endregion

    #region Text Format & Foreground

    private void ApplyTextFormatIfNeeded()
    {
        if (!_textFormatDirty) return;
        if (_textFormat == null) return;

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

        _textFormatDirty = false;
    }

    private void ApplyTextForeground()
    {
        if (Foreground is SolidColorBrush colorBrush)
        {
            _textColor = colorBrush.Color;
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

        try { ds.DrawTextLayout(_staticTextLayout, 0, 0, _textColor); }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is ArgumentException) { }
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
        float w = (float)resourceCreator.Size.Width;
        float h = (float)resourceCreator.Size.Height;

        // 文字和尺寸都没变则跳过重建
        if (_newTextLayout != null
            && _cachedLayoutText == _newText
            && Math.Abs(_cachedLayoutWidth - w) < 0.5f
            && Math.Abs(_cachedLayoutHeight - h) < 0.5f)
            return;

        _newTextLayout?.Dispose();
        _newTextLayout = new CanvasTextLayout(resourceCreator, _newText, _textFormat, w, h);
        _newTextLayout.Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap;
        _newTextLayout.VerticalAlignment = CanvasVerticalAlignment.Center;

        _cachedLayoutText = _newText;
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
        // 空闲状态不做任何事，连 Invalidate 也不调用
        if (_currentState == AnimatedTextBlockRedrawState.Idle)
            return;

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