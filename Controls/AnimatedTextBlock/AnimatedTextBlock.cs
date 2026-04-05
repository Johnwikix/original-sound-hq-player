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
using System.Threading;
using Windows.UI;
using Windows.UI.Text;

namespace WinUIMusicPlayer.Controls;

[TemplatePart(Name = "ContentBorder", Type = typeof(Border))]
[TemplatePart(Name = "AnimatedCanvas", Type = typeof(CanvasAnimatedControl))]
public sealed partial class AnimatedTextBlock : Control
{
    private CanvasAnimatedControl _animatedCanvas = null;

    private string _oldText = string.Empty;
    private string _newText = string.Empty;

    private AnimatedTextBlockRedrawState _currentState = AnimatedTextBlockRedrawState.Idle;
    private TimeSpan _animationBeginTime;

    private List<TextDiffResult> _diffResults = null;

    private CanvasTextFormat _textFormat = new CanvasTextFormat();
    private CanvasLinearGradientBrush _textBrush;
    private Color _textColor = Colors.Black;

    private CanvasTextLayout _oldTextLayout;
    private CanvasTextLayout _newTextLayout;

    // FIX: 缓存 Idle 状态下的静态文字 layout，避免每帧重建
    private CanvasTextLayout _staticTextLayout;
    private bool _staticLayoutDirty = true;

    private ITextEffect _textEffect;

    private float _fontSize = 14;
    private string _fontFamily = FontFamily.XamlAutoFontFamily.Source;
    private FontStretch _fontStretch = FontStretch.Normal;
    private FontStyle _fontStyle = FontStyle.Normal;
    private FontWeight _fontWeight = FontWeights.Normal;

    private TextAlignment _textAlignment = TextAlignment.Left;
    private AnimatedTextBlockTextDirection _textDirection = AnimatedTextBlockTextDirection.LeftToRightThenTopToBottom;
    private TextTrimming _textTrimming = TextTrimming.None;
    private TextWrapping _textWrapping = TextWrapping.NoWrap;
    private readonly Lock _layoutLock = new();
    private bool _textFormatDirty = true;

    #region Properties

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(AnimatedTextBlock), new PropertyMetadata(default(string)));

    public string Text
    {
        get { return (string)GetValue(TextProperty); }
        set
        {
            _oldText = _newText ?? string.Empty;
            _newText = value ?? string.Empty;

            SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);

            SetValue(TextProperty, value);
        }
    }

    public static readonly DependencyProperty TextEffectProperty = DependencyProperty.Register(
        nameof(TextEffect), typeof(ITextEffect), typeof(AnimatedTextBlock), new PropertyMetadata(default(ITextEffect)));

    public ITextEffect TextEffect
    {
        get { return (ITextEffect)GetValue(TextEffectProperty); }
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
        get { return (TextAlignment)GetValue(TextAlignmentProperty); }
        set
        {
            _textAlignment = value;
            SetValue(TextAlignmentProperty, value);
        }
    }

    public static readonly DependencyProperty TextDirectionProperty = DependencyProperty.Register(
        nameof(TextDirection), typeof(AnimatedTextBlockTextDirection), typeof(AnimatedTextBlock), new PropertyMetadata(default(AnimatedTextBlockTextDirection)));

    public AnimatedTextBlockTextDirection TextDirection
    {
        get { return (AnimatedTextBlockTextDirection)GetValue(TextDirectionProperty); }
        set
        {
            _textDirection = value;
            SetValue(TextDirectionProperty, value);
        }
    }

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming), typeof(TextTrimming), typeof(AnimatedTextBlock), new PropertyMetadata(default(TextTrimming)));

    public TextTrimming TextTrimming
    {
        get { return (TextTrimming)GetValue(TextTrimmingProperty); }
        set
        {
            _textTrimming = value;
            SetValue(TextTrimmingProperty, value);
        }
    }

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(AnimatedTextBlock), new PropertyMetadata(default(TextWrapping)));

    public TextWrapping TextWrapping
    {
        get { return (TextWrapping)GetValue(TextWrappingProperty); }
        set
        {
            _textWrapping = value;
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
        this.RegisterPropertyChangedCallback(AnimatedTextBlock.ForegroundProperty, ForegroundChangedCallback);
        this.RegisterPropertyChangedCallback(AnimatedTextBlock.FontFamilyProperty, FontFamilyChangedCallback);
        this.RegisterPropertyChangedCallback(AnimatedTextBlock.FontSizeProperty, FontSizeChangedCallback);
        this.RegisterPropertyChangedCallback(AnimatedTextBlock.FontStretchProperty, FontStretchChangedCallback);
        this.RegisterPropertyChangedCallback(AnimatedTextBlock.FontStyleProperty, FontStyleChangedCallback);
        this.RegisterPropertyChangedCallback(AnimatedTextBlock.FontWeightProperty, FontWeightChangedCallback);

        _textFormat.TrimmingSign = CanvasTrimmingSign.Ellipsis;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _animatedCanvas = GetTemplateChild("AnimatedCanvas") as CanvasAnimatedControl;
        this.SizeChanged += OnSizeChanged;

        ApplyTextFormat();
        ApplyTextForeground();

        if (_animatedCanvas != null)
        {
            // FIX: 初始设为 Paused，避免启动时空转
            _animatedCanvas.Paused = true;
            _animatedCanvas.CreateResources += AnimatedCanvas_CreateResources;
            _animatedCanvas.Update += AnimatedCanvas_Update;
            _animatedCanvas.Draw += AnimatedCanvas_Draw;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _newText = Text ?? string.Empty;
        SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
    }

    // FIX: 页面卸载时清理资源，防止长时间运行后的泄漏
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_animatedCanvas != null)
        {
            _animatedCanvas.Paused = true;
            _animatedCanvas.CreateResources -= AnimatedCanvas_CreateResources;
            _animatedCanvas.Update -= AnimatedCanvas_Update;
            _animatedCanvas.Draw -= AnimatedCanvas_Draw;
        }

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

        _staticLayoutDirty = true;

        // FIX: 不在 UI 线程 dispose layout，避免与 Canvas 线程竞争
        // GenerateNewTextLayout / GenerateOldTextLayout 内部会 lock + dispose 旧的
        SetRedrawState(AnimatedTextBlockRedrawState.LayoutChanged);
    }

    #region Property Changed Callbacks

    private void ForegroundChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        ApplyTextForeground();
        _staticLayoutDirty = true;
        // FIX: 前景色变了需要重绘一帧
        RequestSingleRedraw();
    }

    private void FontFamilyChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontFamily = FontFamily.Source;
        _staticLayoutDirty = true;
    }

    private void FontSizeChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontSize = (float)FontSize;
        _textFormatDirty = true;
        _staticLayoutDirty = true;
    }

    private void FontStretchChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontStretch = FontStretch;
        _staticLayoutDirty = true;
    }

    private void FontStyleChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontStyle = FontStyle;
        _staticLayoutDirty = true;
    }

    private void FontWeightChangedCallback(DependencyObject sender, DependencyProperty dp)
    {
        _fontWeight = FontWeight;
        _staticLayoutDirty = true;
    }

    #endregion

    #region Canvas Events

    private void AnimatedCanvas_CreateResources(CanvasAnimatedControl sender,
        CanvasCreateResourcesEventArgs args)
    {
        if (Foreground is LinearGradientBrush linearGradientBrush)
        {
            var stops = new CanvasGradientStop[linearGradientBrush.GradientStops.Count];

            for (int i = 0; i < linearGradientBrush.GradientStops.Count; i++)
            {
                var gradientStop = linearGradientBrush.GradientStops[i];
                stops[i].Color = gradientStop.Color;
                stops[i].Position = (float)gradientStop.Offset;
            }

            _textBrush?.Dispose();
            _textBrush = new CanvasLinearGradientBrush(sender, stops);
        }
    }

    private void AnimatedCanvas_Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        if (_textEffect == null)
        {
            // FIX: 无动画效果时直接进 Idle，下面会 Pause
            SetRedrawState(AnimatedTextBlockRedrawState.Idle);
            return;
        }

        if (_currentState == AnimatedTextBlockRedrawState.LayoutChanged)
        {
            ApplyTextFormat();

            if (sender.Size.Width <= 0 || sender.Size.Height <= 0)
            {
                SetRedrawState(AnimatedTextBlockRedrawState.Idle);
                return;
            }

            // 只重建 newTextLayout，不做 diff，不跑动画
            GenerateNewTextLayout(sender);

            // 静态 layout 也标脏，Draw 里会用新尺寸重建
            _staticLayoutDirty = true;

            SetRedrawState(AnimatedTextBlockRedrawState.Idle);
            // Idle 会触发 Paused = true，Draw 还会再跑一帧把新 layout 画出来
            return;
        }

        if (_currentState == AnimatedTextBlockRedrawState.TextChanged)
        {
            ApplyTextFormatIfNeeded();   // 只在格式真的变了才重建
            GenerateOldTextLayout(sender);
            GenerateNewTextLayout(sender);
            GenerateDiffResults();
            _animationBeginTime = args.Timing.TotalTime;
            SetRedrawState(AnimatedTextBlockRedrawState.Animating);
        }

        if (_currentState == AnimatedTextBlockRedrawState.Animating)
        {
            UpdateAllClusterProgress(args.Timing);
        }

        _textEffect.Update(_oldText,
            _newText,
            _diffResults,
            _oldTextLayout,
            _newTextLayout,
            _currentState,
            sender,
            args);
    }

    private void AnimatedCanvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        args.DrawingSession.Clear(Colors.Transparent);

        // 在 lock 内取快照，lock 外使用，避免长时间持锁阻塞 UI 线程
        CanvasTextLayout oldSnap, newSnap;
        lock (_layoutLock)
        {
            oldSnap = _oldTextLayout;
            newSnap = _newTextLayout;
        }

        if (_textEffect == null)
        {
            // 无动画效果：直接画静态 layout
            lock (_layoutLock)
            {
                if (_staticLayoutDirty || _staticTextLayout == null)
                {
                    if (sender.Size.Width <= 0 || sender.Size.Height <= 0) return;
                    _staticTextLayout?.Dispose();
                    _staticTextLayout = new CanvasTextLayout(sender,
                        _newText, _textFormat,
                        (float)sender.Size.Width,
                        (float)sender.Size.Height);
                    _staticTextLayout.Options = CanvasDrawTextOptions.EnableColorFont;
                    _staticLayoutDirty = false;
                }
                newSnap = _staticTextLayout;
            }

            if (newSnap == null) return;

            try { args.DrawingSession.DrawTextLayout(newSnap, 0, 0, _textColor); }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is ArgumentException) { }
            return;
        }

        // 有动画效果：newSnap 必须有效
        if (newSnap == null) return;

        try
        {
            _textEffect.DrawText(
                _oldText, _newText,
                _diffResults,
                oldSnap, newSnap,      // ← 传快照，不传字段本身
                _textFormat, _textColor, _textBrush,
                _currentState,
                args.DrawingSession, args);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is ArgumentException) { }
    }

    #endregion

    private void ApplyTextFormat()
    {
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
            if (_animatedCanvas != null)
            {
                var stops = new CanvasGradientStop[linearGradientBrush.GradientStops.Count];

                // FIX: 原代码只创建了 stop 对象但没有赋值到数组
                for (int i = 0; i < linearGradientBrush.GradientStops.Count; i++)
                {
                    stops[i] = new CanvasGradientStop()
                    {
                        Color = linearGradientBrush.GradientStops[i].Color,
                        Position = (float)linearGradientBrush.GradientStops[i].Offset
                    };
                }

                _textBrush?.Dispose();
                _textBrush = new CanvasLinearGradientBrush(_animatedCanvas, stops);
            }
        }
        else
        {
            if (Application.Current.Resources["TextFillColorPrimaryBrush"] is SolidColorBrush defaultForegroundBrush)
            {
                _textColor = defaultForegroundBrush.Color;
                _textBrush = null;
            }
        }
    }

    private void GenerateOldTextLayout(ICanvasAnimatedControl resourceCreator)
    {
        _oldTextLayout?.Dispose();
        _oldTextLayout = new CanvasTextLayout(resourceCreator, _oldText, _textFormat,
            (float)(resourceCreator.Size.Width),
            (float)(resourceCreator.Size.Height));
        _oldTextLayout.Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap;
        _oldTextLayout.VerticalAlignment = CanvasVerticalAlignment.Center;
    }

    private void GenerateNewTextLayout(ICanvasAnimatedControl resourceCreator)
    {
        _newTextLayout?.Dispose();
        _newTextLayout = new CanvasTextLayout(resourceCreator, _newText, _textFormat,
            (float)(resourceCreator.Size.Width),
            (float)(resourceCreator.Size.Height));
        _newTextLayout.Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap;
        _newTextLayout.VerticalAlignment = CanvasVerticalAlignment.Center;
    }

    private void GenerateDiffResults()
    {
        var oldGraphemeClusters = TextRenderingHelper.GenerateGraphemeClusters(_oldText, _oldTextLayout);
        var newGraphemeClusters = TextRenderingHelper.GenerateGraphemeClusters(_newText, _newTextLayout);

        _diffResults = GraphemeClusterDiff.Diff(oldGraphemeClusters, newGraphemeClusters);
    }

    private void UpdateAllClusterProgress(CanvasTimingInformation timing)
    {
        var animationDuration = _textEffect?.AnimationDuration ?? TimeSpan.FromMilliseconds(600);
        var delayPerCluster = _textEffect?.DelayPerCluster ?? TimeSpan.FromMilliseconds(0);

        float step = (float)(1 / (animationDuration.TotalMilliseconds / timing.ElapsedTime.TotalMilliseconds));

        var delay = delayPerCluster <= animationDuration ? delayPerCluster : animationDuration;

        int insertDelayOffset = 0;
        int moveDelayOffset = 0;
        int removeDelayOffset = 0;
        int updateDelayOffset = 0;

        int ongoingAnimations = 0;

        for (int i = 0; i < _diffResults.Count; i++)
        {
            var diffResult = _diffResults[i];
            var oldCluster = diffResult.OldGlyphCluster;
            var newCluster = diffResult.NewGlyphCluster;

            int delayOffset = 0;

            // FIX: 原代码 Move 和 Remove 的 delayOffset 变量对调了
            switch (diffResult.Type)
            {
                default:
                case AnimatedTextBlockDiffOperationType.Move:
                    delayOffset = moveDelayOffset;
                    moveDelayOffset += 1;
                    break;
                case AnimatedTextBlockDiffOperationType.Insert:
                    delayOffset = insertDelayOffset;
                    insertDelayOffset += 1;
                    break;
                case AnimatedTextBlockDiffOperationType.Remove:
                    delayOffset = removeDelayOffset;
                    removeDelayOffset += 1;
                    break;
                case AnimatedTextBlockDiffOperationType.Update:
                    delayOffset = updateDelayOffset;
                    updateDelayOffset += 1;
                    break;
            }

            if (!UpdateClusterProgress(oldCluster, delayOffset, step, delay, timing))
                ongoingAnimations += 1;

            if (!UpdateClusterProgress(newCluster, delayOffset, step, delay, timing))
                ongoingAnimations += 1;
        }

        if (ongoingAnimations < 1)
        {
            SetRedrawState(AnimatedTextBlockRedrawState.Idle);
        }
    }

    private bool UpdateClusterProgress(GraphemeCluster cluster,
        int offset,
        float step,
        TimeSpan delay,
        CanvasTimingInformation timing)
    {
        if (cluster == null)
            return true;

        var duration = _textEffect?.AnimationDuration ?? TimeSpan.FromMilliseconds(0);

        bool isFinished = timing.TotalTime.TotalMilliseconds >=
                          (_animationBeginTime.TotalMilliseconds +
                           delay.TotalMilliseconds * offset +
                           duration.TotalMilliseconds);

        if (isFinished)
        {
            cluster.Progress = 1.0f;
            cluster.IsAnimationFinished = true;
            return true;
        }

        float progress = cluster.Progress + step;

        if (timing.TotalTime.TotalMilliseconds - _animationBeginTime.TotalMilliseconds <
            delay.TotalMilliseconds * offset)
        {
            progress = 0;
        }

        progress = Math.Clamp(progress, 0, 1.0f);
        cluster.Progress = progress;

        return false;
    }

    private void ResetAllClusterProgress()
    {
        foreach (var diffResult in _diffResults)
        {
            diffResult.OldGlyphCluster.Progress = 0;
            diffResult.NewGlyphCluster.Progress = 0;
        }
    }

    // FIX: 用于 Idle 状态只需重绘一帧的场景（如前景色变化）
    private void RequestSingleRedraw()
    {
        if (_animatedCanvas == null) return;
        _animatedCanvas.Paused = false;
        // 下一帧 Update 会检测到 Idle 然后再次 Pause
    }

    private void ApplyTextFormatIfNeeded()
    {
        if (!_textFormatDirty) return;
        ApplyTextFormat();
        _textFormatDirty = false;
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

    private void SetRedrawState(AnimatedTextBlockRedrawState state, bool fireEvent = true)
    {
        _currentState = state;

        // FIX: 核心修复 —— Idle 时暂停画布，彻底停止 Update/Draw 循环
        if (_animatedCanvas != null)
        {
            bool shouldPause = (state == AnimatedTextBlockRedrawState.Idle);
            _animatedCanvas.Paused = shouldPause;
        }

        if (fireEvent)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal,
                () => RedrawStateChanged?.Invoke(this, _currentState));
        }
    }
}