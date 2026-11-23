// Ported from DevWinUI 10.8.0 (MIT), commit 28d9f74fc1ad86d6597b6ad43b6122e52a1b49aa.
// See Libraries/DevWinUI/LICENSE-Controls.txt and README.md for scope and local changes.
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

// https://github.com/cnbluefire

using Microsoft.UI.Xaml.Markup;

namespace WinUIMusicPlayer.Controls;

/// <summary>Redirects a child into a composition surface while retaining its XAML layout.</summary>
[ContentProperty(Name = nameof(Child))]
public partial class RedirectVisualView : Control
{
    /// <summary>Initializes a new instance of the RedirectVisualView class.</summary>
    public RedirectVisualView()
    {
        this.DefaultStyleKey = typeof(RedirectVisualView);

        this.Loaded += RedirectVisualView_Loaded;
        this.Unloaded += RedirectVisualView_Unloaded;
        RegisterPropertyChangedCallback(PaddingProperty, OnPaddingPropertyChanged);
    }

    /// <summary>Gets whether the source offset is applied to the surface brush.</summary>
    protected virtual bool ChildVisualBrushOffsetEnabled => true;


    private bool measureChildInBoundingBox = true;

    /// <summary>Gets or sets whether the child is measured within the viewport.</summary>
    protected bool MeasureChildInBoundingBox
    {
        get => measureChildInBoundingBox;
        set
        {
            if (measureChildInBoundingBox != value)
            {
                measureChildInBoundingBox = value;
                UpdateMeasureChildInBoundingBox();
            }
        }
    }

    /// <summary>Gets whether the composition surface is attached.</summary>
    protected bool RedirectVisualAttached => attached;

    /// <summary>Gets or sets whether the loaded child is redirected.</summary>
    protected bool RedirectVisualEnabled
    {
        get => redirectVisualEnabled;
        set
        {
            if (redirectVisualEnabled != value)
            {
                redirectVisualEnabled = value;

                if (value)
                {
                    if (IsLoaded)
                    {
                        AttachVisuals();
                    }
                }
                else
                {
                    DetachVisuals();
                }
            }
        }
    }


    private bool attached;
    private bool redirectVisualEnabled = true;

    private Grid? layoutRoot;
    private ContentPresenter? childPresenter;
    private Grid? childPresenterContainer;
    private Canvas? ChildHost;
    private Canvas? opacityMaskContainer;


    /// <summary>Gets the LayoutRoot template part.</summary>
    protected Grid? LayoutRoot
    {
        get => layoutRoot;
        private set
        {
            if (layoutRoot != value)
            {
                var old = layoutRoot;

                layoutRoot = value;

                if (old != null)
                {
                    old.SizeChanged -= LayoutRoot_SizeChanged;
                }

                if (layoutRoot != null)
                {
                    layoutRoot.SizeChanged += LayoutRoot_SizeChanged;
                }
            }
        }
    }

    /// <summary>Gets the ChildPresenter template part.</summary>
    protected ContentPresenter? ChildPresenter
    {
        get => childPresenter;
        private set
        {
            if (childPresenter != value)
            {
                var old = childPresenter;

                childPresenter = value;

                if (old != null)
                {
                    old.SizeChanged -= ChildPresenter_SizeChanged;
                }

                if (childPresenter != null)
                {
                    childPresenter.SizeChanged += ChildPresenter_SizeChanged;
                }
            }
        }
    }

    /// <summary>Gets the ChildPresenterContainer template part.</summary>
    protected Grid? ChildPresenterContainer
    {
        get => childPresenterContainer;
        private set
        {
            if (childPresenterContainer != value)
            {
                childPresenterContainer = value;

                UpdateMeasureChildInBoundingBox();
            }
        }
    }


    /// <summary>Gets the OpacityMaskContainer template part.</summary>
    protected Canvas? OpacityMaskContainer
    {
        get => opacityMaskContainer;
        private set => opacityMaskContainer = value;
    }

    private CompositionVisualSurface? childVisualSurface;
    private CompositionSurfaceBrush? childVisualBrush;
    private SpriteVisual? redirectVisual;
    private ExpressionAnimation? offsetBind;

    /// <summary>Gets the brush that samples the child visual after the first attach.</summary>
    protected CompositionBrush? ChildVisualBrush => childVisualBrush;

    /// <summary>Gets the redirected output visual after the first attach.</summary>
    protected SpriteVisual? RootVisual => redirectVisual;

    private void EnsureCompositionResources()
    {
        if (redirectVisual is not null) return;

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var surface = compositor.CreateVisualSurface();
        var brush = compositor.CreateSurfaceBrush(surface);
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;
        brush.Stretch = CompositionStretch.None;

        ExpressionAnimation? offset = null;
        if (ChildVisualBrushOffsetEnabled)
            offset = compositor.CreateExpressionAnimation("Vector2(visual.Offset.X, visual.Offset.Y)");

        var visual = compositor.CreateSpriteVisual();
        visual.RelativeSizeAdjustment = Vector2.One;
        visual.Brush = brush;

        childVisualSurface = surface;
        childVisualBrush = brush;
        offsetBind = offset;
        redirectVisual = visual;
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        DetachVisuals();

        LayoutRoot = GetTemplateChild(nameof(LayoutRoot)) as Grid;
        ChildPresenter = GetTemplateChild(nameof(ChildPresenter)) as ContentPresenter;
        ChildPresenterContainer = GetTemplateChild(nameof(ChildPresenterContainer)) as Grid;
        ChildHost = GetTemplateChild(nameof(ChildHost)) as Canvas;
        OpacityMaskContainer = GetTemplateChild(nameof(OpacityMaskContainer)) as Canvas;

        if (RedirectVisualEnabled)
        {
            AttachVisuals();
        }
    }

    /// <summary>Gets or sets the XAML child to display.</summary>
    public UIElement? Child
    {
        get { return (UIElement?)GetValue(ChildProperty); }
        set { SetValue(ChildProperty, value); }
    }

    /// <summary>Identifies the Child dependency property.</summary>
    public static readonly DependencyProperty ChildProperty =
        DependencyProperty.Register("Child", typeof(UIElement), typeof(RedirectVisualView), new PropertyMetadata(null, static (sender, _) => ((RedirectVisualView)sender).OnUpdateSize()));

    private void AttachVisuals()
    {
        // 模板未就绪或卸载时不挂接，避免空模板被误记为已挂接。
        if (attached || !IsLoaded || LayoutRoot is null || ChildPresenter is null || ChildHost is null) return;

        EnsureCompositionResources();
        attached = true;

        if (LayoutRoot != null)
        {
            if (ChildPresenter != null)
            {
                var childBorderVisual = ElementCompositionPreview.GetElementVisual(ChildPresenter);

                childVisualSurface!.SourceVisual = childBorderVisual;

                if (offsetBind != null)
                {
                    offsetBind.SetReferenceParameter("visual", childBorderVisual);
                    childVisualBrush!.StartAnimation("Offset", offsetBind);
                }
            }

            if (ChildPresenterContainer != null)
            {
                ElementCompositionPreview.GetElementVisual(ChildPresenterContainer).IsVisible = false;
            }

            if (OpacityMaskContainer != null)
            {
                ElementCompositionPreview.GetElementVisual(OpacityMaskContainer).IsVisible = false;
            }

            if (ChildHost != null)
            {
                ElementCompositionPreview.SetElementChildVisual(ChildHost, redirectVisual);
            }

            UpdateSize();
        }

        OnAttachVisuals();
    }

    private void DetachVisuals()
    {
        if (!attached) return;

        attached = false;

        if (LayoutRoot != null)
        {
            childVisualSurface!.SourceVisual = null;

            if (offsetBind != null)
            {
                childVisualBrush!.StopAnimation("Offset");
                offsetBind.ClearAllParameters();
            }

            if (ChildPresenterContainer != null)
            {
                ElementCompositionPreview.GetElementVisual(ChildPresenterContainer).IsVisible = true;
            }

            if (OpacityMaskContainer != null)
            {
                ElementCompositionPreview.GetElementVisual(OpacityMaskContainer).IsVisible = true;
            }

            if (ChildHost != null)
            {
                ElementCompositionPreview.SetElementChildVisual(ChildHost, null);
            }
        }

        OnDetachVisuals();
    }

    private void RedirectVisualView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) DispatcherQueue.TryEnqueue(DetachVisualsIfUnloaded);
        else DetachVisuals();
    }

    private void DetachVisualsIfUnloaded()
    {
        if (!IsLoaded) DetachVisuals();
    }

    private void RedirectVisualView_Loaded(object sender, RoutedEventArgs e)
    {
        if (RedirectVisualEnabled)
        {
            AttachVisuals();
        }
    }

    private void OnPaddingPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdateSize();
    }

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSize();
    }


    private void ChildPresenter_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSize();
    }

    private void UpdateSize()
    {
        if (attached && LayoutRoot != null)
        {
            if (ChildPresenter != null)
            {
                childVisualSurface!.SourceSize = new Vector2((float)ChildPresenter.ActualWidth, (float)ChildPresenter.ActualHeight);
            }
        }

        OnUpdateSize();
    }

    private void UpdateMeasureChildInBoundingBox()
    {
        if (ChildPresenterContainer != null)
        {
            var value = MeasureChildInBoundingBox;

            var length = new GridLength(1, value ? GridUnitType.Star : GridUnitType.Auto);

            if (ChildPresenterContainer.RowDefinitions.Count > 0)
            {
                ChildPresenterContainer.RowDefinitions[0].Height = length;
            }
            if (ChildPresenterContainer.ColumnDefinitions.Count > 0)
            {
                ChildPresenterContainer.ColumnDefinitions[0].Width = length;
            }
        }
    }

    /// <summary>Updates derived visuals when the surface is attached.</summary>
    protected virtual void OnAttachVisuals()
    {

    }

    /// <summary>Updates derived visuals when the surface is detached.</summary>
    protected virtual void OnDetachVisuals()
    {

    }

    /// <summary>Updates derived visuals when the viewport or child changes size.</summary>
    protected virtual void OnUpdateSize()
    {

    }
}
