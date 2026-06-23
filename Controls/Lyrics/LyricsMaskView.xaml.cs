// Copyright (c) OriginalSound HIFI Player project
// Forked from WinUI Gallery (Microsoft Corporation, MIT License):
//   https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/Controls/OpacityMaskView.xaml.cs
// Originally derived from Windows Community Toolkit Labs PR #491.
//
// Modifications vs WinUI Gallery:
//   - GetVisualBrush: visual.Opacity = 0  →  visual.Opacity = 0.01
//     This is an experiment: with Opacity = 0, the WinUI 3 hit-testing system
//     skips the ContentPresenter subtree (Composition-tree based hit testing),
//     which prevents a nested ScrollView from receiving PointerPressed events
//     (ScrollView goes through XAML pointer events, not the ManipulationMode=System
//     system path that ScrollViewer uses). Trying 0.01 to keep the visual nearly
//     invisible while hoping the hit-test threshold treats it as visible.

using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace WinUIMusicPlayer.Controls.Lyrics;

/// <summary>
/// A control that applies an opacity mask to its content.
/// Forked from WinUI Gallery's <c>OpacityMaskView</c>; see header for deltas.
/// </summary>
[TemplatePart(Name = RootGridTemplateName, Type = typeof(Grid))]
[TemplatePart(Name = MaskContainerTemplateName, Type = typeof(Border))]
[TemplatePart(Name = ContentPresenterTemplateName, Type = typeof(ContentPresenter))]
public sealed partial class LyricsMaskView : ContentControl
{
    /// <summary>
    /// Identifies the <see cref="OpacityMask"/> property.
    /// </summary>
    public static readonly DependencyProperty OpacityMaskProperty =
        DependencyProperty.Register(nameof(OpacityMask), typeof(UIElement), typeof(LyricsMaskView), new PropertyMetadata(null, OnOpacityMaskChanged));

    private const string ContentPresenterTemplateName = "PART_ContentPresenter";
    private const string MaskContainerTemplateName = "PART_MaskContainer";
    private const string RootGridTemplateName = "PART_RootGrid";

    private readonly Compositor _compositor = CompositionTarget.GetCompositorForCurrentThread();
    private CompositionBrush? _mask;
    private CompositionMaskBrush? _maskBrush;

    public LyricsMaskView()
    {
        DefaultStyleKey = typeof(LyricsMaskView);
    }

    /// <summary>
    /// Gets or sets a <see cref="UIElement"/> as the opacity mask that is applied to alpha-channel masking for the rendered content of the content.
    /// </summary>
    public UIElement? OpacityMask
    {
        get => (UIElement?)GetValue(OpacityMaskProperty);
        set => SetValue(OpacityMaskProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        Grid rootGrid = (Grid)GetTemplateChild(RootGridTemplateName);
        ContentPresenter contentPresenter = (ContentPresenter)GetTemplateChild(ContentPresenterTemplateName);
        Border maskContainer = (Border)GetTemplateChild(MaskContainerTemplateName);

        _maskBrush = _compositor.CreateMaskBrush();
        _maskBrush.Source = GetVisualBrush(contentPresenter);
        _mask = GetVisualBrush(maskContainer);
        _maskBrush.Mask = OpacityMask is null ? null : _mask;

        SpriteVisual redirectVisual = _compositor.CreateSpriteVisual();
        redirectVisual.RelativeSizeAdjustment = Vector2.One;
        redirectVisual.Brush = _maskBrush;
        ElementCompositionPreview.SetElementChildVisual(rootGrid, redirectVisual);
    }

    private static CompositionBrush GetVisualBrush(UIElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);

        Compositor compositor = visual.Compositor;

        CompositionVisualSurface visualSurface = compositor.CreateVisualSurface();
        visualSurface.SourceVisual = visual;
        ExpressionAnimation sourceSizeAnimation = compositor.CreateExpressionAnimation($"{nameof(visual)}.Size");
        sourceSizeAnimation.SetReferenceParameter(nameof(visual), visual);
        visualSurface.StartAnimation(nameof(visualSurface.SourceSize), sourceSizeAnimation);

        CompositionSurfaceBrush brush = compositor.CreateSurfaceBrush(visualSurface);

        // Experimental: keep the element nearly invisible for the visual compositor
        // (avoids double-rendering with the redirect sprite visual) but slightly
        // non-zero so that WinUI 3 hit testing still walks the subtree.
        visual.Opacity = 0.001f;

        return brush;
    }

    private static void OnOpacityMaskChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        LyricsMaskView self = (LyricsMaskView)d;
        if (self._maskBrush is not { } maskBrush)
        {
            return;
        }

        UIElement? opacityMask = (UIElement?)e.NewValue;
        maskBrush.Mask = opacityMask is null ? null : self._mask;
    }
}
