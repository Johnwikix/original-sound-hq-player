// Ported from DevWinUI 10.8.0 (MIT), commit 28d9f74fc1ad86d6597b6ad43b6122e52a1b49aa.
// See Libraries/DevWinUI/LICENSE-Controls.txt and README.md for scope and local changes.
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

// https://github.com/cnbluefire

namespace WinUIMusicPlayer.Controls;
/// <summary>Scrolls overflowing content with two composition copies while playback is enabled.</summary>
public partial class AutoScrollView : RedirectVisualView
{
    /// <summary>Initializes a new instance of the AutoScrollView class.</summary>
    public AutoScrollView()
    {
        RedirectVisualEnabled = false;

        compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

        propSet = compositor.CreatePropertySet();
        propSet.InsertScalar(nameof(Spacing), (float)Spacing);

        visual1 = compositor.CreateSpriteVisual();
        visual1.Brush = ChildVisualBrush;

        visual2 = compositor.CreateSpriteVisual();
        visual2.Brush = ChildVisualBrush;

        offsetBind1 = compositor.CreateExpressionAnimation("Vector3(visual.Offset.X, visual.Offset.Y, 0)");
        offsetBind2 = compositor.CreateExpressionAnimation("Vector3(visual.Offset.X + visual.Size.X + propSet.Spacing, visual.Offset.Y, 0)");

        offsetBind2.SetReferenceParameter("propSet", propSet);

        sizeBind = compositor.CreateExpressionAnimation("visual.Size");

        RootVisual.Brush = null;
        RootVisual.Children.InsertAtTop(visual2);
        RootVisual.Children.InsertAtTop(visual1);
        RootVisual.IsPixelSnappingEnabled = true;

        linearEasingFunc = compositor.CreateLinearEasingFunction();

        animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0);
        animation.InsertExpressionKeyFrame(1, "-visual.Size.X - propSet.Spacing", linearEasingFunc);
        animation.Duration = TimeSpan.FromSeconds(1);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.SetReferenceParameter("propSet", propSet);

        MeasureChildInBoundingBox = !IsPlaying;
        updateAnimationHandler = UpdateAnimationState;

        this.Loaded += AutoScrollView_Loaded;
        this.Unloaded += AutoScrollView_Unloaded;
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueHandler updateAnimationHandler;
    private bool updateQueued;
    private InsetClip? viewportClip;
    private CompositionClip? previousClip;

    private Compositor compositor;

    private CompositionPropertySet propSet;

    private SpriteVisual visual1;
    private SpriteVisual visual2;

    private ExpressionAnimation offsetBind1;
    private ExpressionAnimation offsetBind2;
    private ExpressionAnimation sizeBind;

    private LinearEasingFunction linearEasingFunc;
    private ScalarKeyFrameAnimation animation;

    /// <inheritdoc/>
    protected override bool ChildVisualBrushOffsetEnabled => false;

    /// <summary>Gets or sets the gap in DIPs between scrolling copies. The default is 20.</summary>
    public double Spacing
    {
        get { return (double)GetValue(SpacingProperty); }
        set { SetValue(SpacingProperty, value); }
    }

    /// <summary>Gets or sets whether overflowing content scrolls. The default is true.</summary>
    public bool IsPlaying
    {
        get { return (bool)GetValue(IsPlayingProperty); }
        set { SetValue(IsPlayingProperty, value); }
    }

    /// <summary>Gets or sets the scrolling speed in DIPs per second. The upstream spelling is retained; the default is 30.</summary>
    public double ScrollingPixelsPreSecond
    {
        get { return (double)GetValue(ScrollingPixelsPreSecondProperty); }
        set { SetValue(ScrollingPixelsPreSecondProperty, value); }
    }

    /// <summary>Identifies the Spacing dependency property.</summary>
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register("Spacing", typeof(double), typeof(AutoScrollView), new PropertyMetadata(20d, (s, a) =>
        {
            if (s is AutoScrollView sender && !Equals(a.NewValue, a.OldValue))
            {
                var value = Convert.ToSingle(a.NewValue);
                if (!float.IsFinite(value) || value < 0) throw new ArgumentException(nameof(Spacing));

                sender.propSet.InsertScalar(nameof(Spacing), value);
            }
        }));


    /// <summary>Identifies the IsPlaying dependency property.</summary>
    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register("IsPlaying", typeof(bool), typeof(AutoScrollView), new PropertyMetadata(true, (s, a) =>
        {
            if (s is AutoScrollView sender && !Equals(a.NewValue, a.OldValue))
            {
                sender.UpdateAnimationState();
            }
        }));


    /// <summary>Identifies the ScrollingPixelsPreSecond dependency property.</summary>
    public static readonly DependencyProperty ScrollingPixelsPreSecondProperty =
        DependencyProperty.Register("ScrollingPixelsPreSecond", typeof(double), typeof(AutoScrollView), new PropertyMetadata(30d, (s, a) =>
        {
            if (s is AutoScrollView sender && !Equals(a.NewValue, a.OldValue))
            {
                var value = Convert.ToSingle(a.NewValue);
                if (!float.IsFinite(value) || value <= 0) throw new ArgumentException(nameof(ScrollingPixelsPreSecondProperty));

                sender.UpdateAnimationSpeed();
            }
        }));


    private void AutoScrollView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateAnimationState();
    }


    /// <inheritdoc/>
    protected override void OnAttachVisuals()
    {
        base.OnAttachVisuals();

        if (ChildPresenter != null && LayoutRoot != null)
        {
            var childVisual = ElementCompositionPreview.GetElementVisual(ChildPresenter);
            var rootVisual = ElementCompositionPreview.GetElementVisual(LayoutRoot);

            previousClip = rootVisual.Clip;
            viewportClip = compositor.CreateInsetClip();
            rootVisual.Clip = viewportClip;

            offsetBind1.SetReferenceParameter("visual", childVisual);
            offsetBind2.SetReferenceParameter("visual", childVisual);

            offsetBind2.SetReferenceParameter("propSet", propSet);

            sizeBind.SetReferenceParameter("visual", childVisual);

            animation.SetReferenceParameter("visual", childVisual);
            animation.SetReferenceParameter("visual2", rootVisual);
            animation.SetReferenceParameter("propSet", propSet);
            animation.Duration = GetAnimationDuration();

            visual1.StartAnimation("Offset", offsetBind1);
            visual1.StartAnimation("Size", sizeBind);
            visual2.StartAnimation("Offset", offsetBind2);
            visual2.StartAnimation("Size", sizeBind);

            RootVisual.Offset = Vector3.Zero;
            RootVisual.StartAnimation("Offset.X", animation);
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachVisuals()
    {
        base.OnDetachVisuals();

        visual1.StopAnimation("Offset");
        visual1.StopAnimation("Size");
        visual2.StopAnimation("Offset");
        visual2.StopAnimation("Size");

        RootVisual.StopAnimation("Offset.X");

        offsetBind1.ClearAllParameters();
        offsetBind2.ClearAllParameters();
        sizeBind.ClearAllParameters();
        // 释放对旧模板视觉的引用；重载时重新设置表达式参数。
        animation.ClearAllParameters();
        RootVisual.Offset = Vector3.Zero;
        if (LayoutRoot is not null)
        {
            var root = ElementCompositionPreview.GetElementVisual(LayoutRoot);
            if (root.Clip == viewportClip) root.Clip = previousClip;
        }
        viewportClip?.Dispose();
        viewportClip = null;
        previousClip = null;
    }

    /// <inheritdoc/>
    protected override void OnUpdateSize()
    {
        base.OnUpdateSize();

        // 尺寸事件合并到一次 UI 队列回调；.NET 10+ 复用委托，避免逐帧闭包。
        if (IsLoaded && !updateQueued)
        {
            updateQueued = DispatcherQueue.TryEnqueue(updateAnimationHandler);
        }
    }

    private void UpdateAnimationState()
    {
        updateQueued = false;
        MeasureChildInBoundingBox = !IsPlaying;

        if (IsLoaded
            && IsPlaying
            && ChildPresenter != null
            && LayoutRoot != null)
        {
            var childWidth = ChildPresenter.ActualWidth;
            var rootWidth = LayoutRoot.ActualWidth - Padding.Left - Padding.Right;

            if (childWidth > 0 && rootWidth > 0 && childWidth > rootWidth)
            {
                bool wasAttached = RedirectVisualAttached;
                RedirectVisualEnabled = true;
                if (wasAttached) UpdateAnimationSpeed();
            }
            else
            {
                RedirectVisualEnabled = false;
            }
        }
        else
        {
            RedirectVisualEnabled = false;
        }
    }

    private void AutoScrollView_Unloaded(object sender, RoutedEventArgs e)
    {
        // 基类停止所有动画并释放源视觉引用；迟到队列回调也会检查 IsLoaded。
        updateQueued = false;
    }

    private TimeSpan GetAnimationDuration() =>
        TimeSpan.FromSeconds(Math.Clamp((ChildPresenter?.ActualWidth ?? 0) / ScrollingPixelsPreSecond, 0.001, 86400));

    private void UpdateAnimationSpeed()
    {
        if (RedirectVisualAttached && ChildPresenter != null)
        {
            var duration = GetAnimationDuration();
            if (animation.Duration == duration) return;
            var progress = 0f;
            var animationController = RootVisual.TryGetAnimationController("Offset.X");
            if (animationController != null)
            {
                animationController.Pause();
                progress = animationController.Progress;
            }
            RootVisual.StopAnimation("Offset.X");

            animation.Duration = duration;
            RootVisual.StartAnimation("Offset.X", animation);

            if (progress > 0)
            {
                animationController = RootVisual.TryGetAnimationController("Offset.X");
                if (animationController != null)
                {
                    animationController.Pause();
                    animationController.Progress = progress;
                    animationController.Resume();
                }
            }
        }
    }
}
