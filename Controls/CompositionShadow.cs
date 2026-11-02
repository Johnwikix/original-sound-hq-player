// Ported from DevWinUI 10.8.0 (MIT), commit 28d9f74fc1ad86d6597b6ad43b6122e52a1b49aa.
// See Libraries/DevWinUI/LICENSE-Controls.txt and README.md for scope and local changes.
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace WinUIMusicPlayer.Controls;

/// <summary>Draws a composition shadow behind content, optionally using its glyph or image alpha mask.</summary>
/// <remarks>Includes rounded masks, breathing animation and image edge color. Caller-provided masks remain caller-owned.</remarks>
[ContentProperty(Name = nameof(Content))]
[TemplatePart(Name = ShadowElementName, Type = typeof(Border))]
public partial class CompositionShadow : Control
{
    private const string ShadowElementName = "PART_ShadowElement";
    private Border? _shadowElement;
    private FrameworkElement? _observedContent;
    private CompositionBrush? _customMask;
    private CompositionGeometry? _maskGeometry;
    private CompositionSpriteShape? _maskShape;
    private ShapeVisual? _maskVisual;
    private CompositionVisualSurface? _maskSurface;
    private CompositionSurfaceBrush? _maskBrush;
    private CompositionColorBrush? _maskFill;
    private CompositionGeometricClip? _contentClip;
    private Microsoft.UI.Composition.Visual? _clippedContent;
    private CompositionClip? _previousContentClip;
    private ScalarKeyFrameAnimation? _breathAnimation;
    private CubicBezierEasingFunction? _ownedBreathEasing;
    private CompositionEasingFunction? _breathEasing;
    private TimeSpan? _breathDuration;
    private bool _breathRequested;
    private CancellationTokenSource? _edgeColorCancellation;
    private long _edgeColorVersion;

    /// <summary>Gets the compositor used by the control, retaining the upstream member name.</summary>
    public readonly Compositor compositor;

    /// <summary>Initializes a new instance of the CompositionShadow class.</summary>
    public CompositionShadow()
    {
        DefaultStyleKey = typeof(CompositionShadow);
        compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        DropShadow = compositor.CreateDropShadow();
        Visual = compositor.CreateSpriteVisual();
        Visual.Shadow = DropShadow;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        UpdateShadow();
    }

    /// <summary>Gets the underlying shadow. Its lifetime belongs to this control.</summary>
    public DropShadow DropShadow { get; }

    /// <summary>Gets the shadow visual. Its lifetime belongs to this control.</summary>
    public SpriteVisual Visual { get; }

    /// <summary>Gets or sets a custom mask, or null to restore automatic content masking.</summary>
    public CompositionBrush? Mask
    {
        get => DropShadow.Mask;
        set
        {
            _customMask = value;
            UpdateMask();
        }
    }

    /// <summary>Gets or sets the content displayed in front of the shadow.</summary>
    public FrameworkElement? Content
    {
        get => (FrameworkElement?)GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    /// <summary>Identifies the Content dependency property.</summary>
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(FrameworkElement), typeof(CompositionShadow),
        new PropertyMetadata(null, static (sender, _) => ((CompositionShadow)sender).OnContentChanged()));

    /// <summary>Gets or sets the blur radius in DIPs. The default is 9.</summary>
    public double BlurRadius
    {
        get => (double)GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    /// <summary>Identifies the BlurRadius dependency property.</summary>
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(CompositionShadow), new PropertyMetadata(9d,
            static (sender, args) => ((CompositionShadow)sender).DropShadow.BlurRadius = (float)(double)args.NewValue));

    /// <summary>Gets or sets the shadow color. The default is black.</summary>
    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Identifies the Color dependency property.</summary>
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(CompositionShadow), new PropertyMetadata(Colors.Black,
            static (sender, args) => ((CompositionShadow)sender).DropShadow.Color = (Color)args.NewValue));

    /// <summary>Gets or sets the horizontal shadow offset in DIPs.</summary>
    public double OffsetX
    {
        get => (double)GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    /// <summary>Identifies the OffsetX dependency property.</summary>
    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
        nameof(OffsetX), typeof(double), typeof(CompositionShadow), new PropertyMetadata(0d, OnOffsetChanged));

    /// <summary>Gets or sets the vertical shadow offset in DIPs.</summary>
    public double OffsetY
    {
        get => (double)GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    /// <summary>Identifies the OffsetY dependency property.</summary>
    public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
        nameof(OffsetY), typeof(double), typeof(CompositionShadow), new PropertyMetadata(0d, OnOffsetChanged));

    /// <summary>Gets or sets the depth offset in DIPs.</summary>
    public double OffsetZ
    {
        get => (double)GetValue(OffsetZProperty);
        set => SetValue(OffsetZProperty, value);
    }

    /// <summary>Identifies the OffsetZ dependency property.</summary>
    public static readonly DependencyProperty OffsetZProperty = DependencyProperty.Register(
        nameof(OffsetZ), typeof(double), typeof(CompositionShadow), new PropertyMetadata(0d, OnOffsetChanged));

    /// <summary>Gets or sets the shadow opacity. The default is 1.</summary>
    public double ShadowOpacity
    {
        get => (double)GetValue(ShadowOpacityProperty);
        set => SetValue(ShadowOpacityProperty, value);
    }

    /// <summary>Identifies the ShadowOpacity dependency property.</summary>
    public static readonly DependencyProperty ShadowOpacityProperty = DependencyProperty.Register(
        nameof(ShadowOpacity), typeof(double), typeof(CompositionShadow), new PropertyMetadata(1d,
            static (sender, args) => ((CompositionShadow)sender).DropShadow.Opacity = (float)(double)args.NewValue));

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        DetachShadow();
        base.OnApplyTemplate();
        _shadowElement = GetTemplateChild(ShadowElementName) as Border;
        if (IsLoaded) AttachShadow();
    }

    private static void OnOffsetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        // 只写变化的属性，避免无关 DP 读取及回写 BlurRadius 导致呼吸动画停止。
        var shadow = ((CompositionShadow)sender).DropShadow;
        var offset = shadow.Offset;
        float value = (float)(double)args.NewValue;
        if (args.Property == OffsetXProperty) offset.X = value;
        else if (args.Property == OffsetYProperty) offset.Y = value;
        else offset.Z = value;
        shadow.Offset = offset;
    }

    private void UpdateShadow()
    {
        DropShadow.BlurRadius = (float)BlurRadius;
        DropShadow.Color = Color;
        DropShadow.Opacity = (float)ShadowOpacity;
        DropShadow.Offset = new Vector3((float)OffsetX, (float)OffsetY, (float)OffsetZ);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ObserveContent();
        AttachShadow();
        if (IsBreathing || _breathRequested) StartBreath(_breathEasing, _breathDuration);
        RefreshEdgeColor();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        StopObservingContent();
        DetachShadow();
        StopBreathCore();
        CancelEdgeColor();
        ReleaseGeometryMask();
        // 自动字形画刷来自 XAML，自定义画刷可能由内外层共享；这里只解绑，不 Dispose。
        DropShadow.Mask = null;
    }

    private void OnContentChanged()
    {
        StopObservingContent();
        ReleaseGeometryMask();
        if (IsLoaded) ObserveContent();
        UpdateMask();
        UpdateSize();
    }

    private void ObserveContent()
    {
        if (_observedContent == Content) return;
        StopObservingContent();
        _observedContent = Content;
        if (_observedContent is not null) _observedContent.SizeChanged += OnSizeChanged;
    }

    private void StopObservingContent()
    {
        if (_observedContent is not null) _observedContent.SizeChanged -= OnSizeChanged;
        _observedContent = null;
    }

    private void AttachShadow()
    {
        if (_shadowElement is not null) ElementCompositionPreview.SetElementChildVisual(_shadowElement, Visual);
        UpdateShadow();
        UpdateMask();
        UpdateSize();
    }

    private void DetachShadow()
    {
        if (_shadowElement is not null) ElementCompositionPreview.SetElementChildVisual(_shadowElement, null);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateSize();
        if (IsRounded || UseCornerRadius) UpdateMask();
    }

    private void UpdateSize() => Visual.Size = new Vector2(
        (float)(Content?.ActualWidth ?? ActualWidth), (float)(Content?.ActualHeight ?? ActualHeight));

    private void UpdateMask()
    {
        if (IsLoaded && (IsRounded || (UseCornerRadius && _customMask is null)))
        {
            UpdateGeometryMask();
            return;
        }

        ReleaseGeometryMask();
        // 自动 mask 仅在加载后取；预先设置的自定义 mask 保留到模板挂接。
        DropShadow.Mask = _customMask ?? (IsLoaded ? Content switch
        {
            TextBlock text => text.GetAlphaMask(),
            Image image => image.GetAlphaMask(),
            Shape shape => shape.GetAlphaMask(),
            _ => null
        } : null);
    }

    private void UpdateGeometryMask()
    {
        bool ellipse = IsRounded;
        if (_maskGeometry is null || ellipse != (_maskGeometry is CompositionEllipseGeometry))
        {
            ReleaseGeometryMask();
            _maskGeometry = ellipse ? compositor.CreateEllipseGeometry() : compositor.CreateRoundedRectangleGeometry();
            _maskFill = compositor.CreateColorBrush(Colors.White);
            _maskShape = compositor.CreateSpriteShape(_maskGeometry);
            _maskShape.FillBrush = _maskFill;
            _maskVisual = compositor.CreateShapeVisual();
            _maskVisual.Shapes.Add(_maskShape);
            _maskSurface = compositor.CreateVisualSurface();
            _maskSurface.SourceVisual = _maskVisual;
            _maskBrush = compositor.CreateSurfaceBrush(_maskSurface);
        }

        var size = new Vector2((float)ActualWidth, (float)ActualHeight);
        if (_maskGeometry is CompositionEllipseGeometry circle)
        {
            circle.Center = size / 2;
            circle.Radius = new Vector2(MathF.Min(size.X, size.Y) / 2);
        }
        else if (_maskGeometry is CompositionRoundedRectangleGeometry rectangle)
        {
            rectangle.Size = size;
            rectangle.CornerRadius = new Vector2((float)CornerRadius);
        }
        _maskVisual!.Size = size;
        _maskSurface!.SourceSize = size;
        DropShadow.Mask = _maskBrush;

        if (Content is not null && _contentClip is null)
        {
            _clippedContent = ElementCompositionPreview.GetElementVisual(Content);
            _previousContentClip = _clippedContent.Clip;
            _contentClip = compositor.CreateGeometricClip(_maskGeometry);
            _clippedContent.Clip = _contentClip;
        }
    }

    private void ReleaseGeometryMask()
    {
        if (_clippedContent is not null && _clippedContent.Clip == _contentClip)
            _clippedContent.Clip = _previousContentClip;
        _clippedContent = null;
        _previousContentClip = null;
        _contentClip?.Dispose();
        _contentClip = null;
        if (_maskBrush is not null) DropShadow.Mask = null;
        _maskBrush?.Dispose();
        _maskBrush = null;
        if (_maskSurface is not null) _maskSurface.SourceVisual = null;
        _maskSurface?.Dispose();
        _maskSurface = null;
        _maskVisual?.Shapes.Clear();
        _maskVisual?.Dispose();
        _maskVisual = null;
        _maskShape?.Dispose();
        _maskShape = null;
        _maskFill?.Dispose();
        _maskFill = null;
        _maskGeometry?.Dispose();
        _maskGeometry = null;
    }

    /// <summary>Gets the underlying drop shadow, matching the upstream method.</summary>
    /// <returns>The shadow owned by this control.</returns>
    public DropShadow GetDropShadow() => DropShadow;

    /// <summary>Starts the upstream 5–30 DIP breathing animation when the control is loaded.</summary>
    /// <param name="breathEasing">An optional caller-owned easing function.</param>
    /// <param name="breathDuration">An optional cycle duration, defaulting to two seconds.</param>
    public void StartBreath(CompositionEasingFunction? breathEasing = null, TimeSpan? breathDuration = null)
    {
        if (_breathAnimation is not null) return;
        _breathRequested = true;
        _breathEasing = breathEasing;
        _breathDuration = breathDuration;
        if (!IsLoaded) return;

        // 与上游 CompositionExtensions.CreateEaseInOutSineEasingFunction 控制点一致。
        _ownedBreathEasing = breathEasing is null
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.445f, 0.050f), new Vector2(0.550f, 0.950f)) : null;
        var easing = breathEasing ?? _ownedBreathEasing!;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 5, easing);
        animation.InsertKeyFrame(0.5f, 30, easing);
        animation.InsertKeyFrame(1, 5, easing);
        animation.Duration = breathDuration ?? TimeSpan.FromSeconds(2);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.Direction = AnimationDirection.Alternate;
        _breathAnimation = animation;
        DropShadow.StartAnimation("BlurRadius", animation);
    }

    /// <summary>Stops breathing and restores the configured blur radius.</summary>
    public void StopBreath()
    {
        _breathRequested = false;
        _breathEasing = null;
        _breathDuration = null;
        StopBreathCore();
    }

    private void StopBreathCore()
    {
        DropShadow.StopAnimation("BlurRadius");
        _breathAnimation?.Dispose();
        _breathAnimation = null;
        _ownedBreathEasing?.Dispose();
        _ownedBreathEasing = null;
        DropShadow.BlurRadius = (float)BlurRadius;
    }

    private void UpdateIsBreathing()
    {
        if (IsBreathing) StartBreath();
        else StopBreath();
    }

    private void CancelEdgeColor()
    {
        _edgeColorVersion++;
        var cancellation = _edgeColorCancellation;
        _edgeColorCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void RefreshEdgeColor()
    {
        CancelEdgeColor();
        if (!UseEdgeColorFromImage || ImageSourceForEdgeColor is null)
        {
            // 与上游一致，关闭取色后回到黑色；Loaded 不覆盖普通 Color 设置。
            return;
        }
        if (!IsLoaded) return;
        var cancellation = new CancellationTokenSource();
        _edgeColorCancellation = cancellation;
        _ = UpdateColorFromImageAsync(_edgeColorVersion, cancellation.Token);
    }

    private async Task UpdateColorFromImageAsync(long version, CancellationToken cancellationToken)
    {
        try
        {
            var color = await CompositionShadowImageHelper.GetEdgeColorAsync(ImageSourceForEdgeColor, cancellationToken);
            if (IsLoaded && !cancellationToken.IsCancellationRequested && version == _edgeColorVersion) Color = color;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // 图像失败不能逃出依赖属性回调，也不能让旧请求覆盖后来的用户选择。
            System.Diagnostics.Debug.WriteLine($"CompositionShadow edge color: {exception}");
            if (IsLoaded && !cancellationToken.IsCancellationRequested && version == _edgeColorVersion) Color = Colors.Black;
        }
        finally
        {
            if (version == _edgeColorVersion)
            {
                _edgeColorCancellation?.Dispose();
                _edgeColorCancellation = null;
            }
        }
    }
}
