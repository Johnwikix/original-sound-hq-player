// Ported from DevWinUI 10.8.0 (MIT). See Libraries/DevWinUI/LICENSE-Controls.txt.
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace WinUIMusicPlayer.Controls;

public partial class CompositionShadow
{
    /// <summary>Gets or sets whether the shadow continuously animates its blur radius.</summary>
    public bool IsBreathing
    {
        get => (bool)GetValue(IsBreathingProperty);
        set => SetValue(IsBreathingProperty, value);
    }

    /// <summary>Identifies the IsBreathing dependency property.</summary>
    public static readonly DependencyProperty IsBreathingProperty = DependencyProperty.Register(
        nameof(IsBreathing), typeof(bool), typeof(CompositionShadow),
        new PropertyMetadata(false, static (sender, _) => ((CompositionShadow)sender).UpdateIsBreathing()));

    /// <summary>Gets or sets whether the shadow and content use the configured corner radius.</summary>
    public bool UseCornerRadius
    {
        get => (bool)GetValue(UseCornerRadiusProperty);
        set => SetValue(UseCornerRadiusProperty, value);
    }

    /// <summary>Identifies the UseCornerRadius dependency property.</summary>
    public static readonly DependencyProperty UseCornerRadiusProperty = DependencyProperty.Register(
        nameof(UseCornerRadius), typeof(bool), typeof(CompositionShadow), new PropertyMetadata(false, OnMaskChanged));

    /// <summary>Gets or sets the uniform corner radius in DIPs, retaining the upstream scalar API.</summary>
    public new double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Identifies the CornerRadius dependency property.</summary>
    public new static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(CompositionShadow), new PropertyMetadata(0d, OnMaskChanged));

    /// <summary>Gets or sets whether a circular mask and clip take precedence over other masks.</summary>
    public bool IsRounded
    {
        get => (bool)GetValue(IsRoundedProperty);
        set => SetValue(IsRoundedProperty, value);
    }

    /// <summary>Identifies the IsRounded dependency property.</summary>
    public static readonly DependencyProperty IsRoundedProperty = DependencyProperty.Register(
        nameof(IsRounded), typeof(bool), typeof(CompositionShadow), new PropertyMetadata(false, OnMaskChanged));

    /// <summary>Gets or sets whether the shadow color is sampled from the image perimeter.</summary>
    public bool UseEdgeColorFromImage
    {
        get => (bool)GetValue(UseEdgeColorFromImageProperty);
        set => SetValue(UseEdgeColorFromImageProperty, value);
    }

    /// <summary>Identifies the UseEdgeColorFromImage dependency property.</summary>
    public static readonly DependencyProperty UseEdgeColorFromImageProperty = DependencyProperty.Register(
        nameof(UseEdgeColorFromImage), typeof(bool), typeof(CompositionShadow), new PropertyMetadata(false, OnEdgeColorChanged));

    /// <summary>Gets or sets an image URI or path string used for edge color sampling.</summary>
    public object? ImageSourceForEdgeColor
    {
        get => GetValue(ImageSourceForEdgeColorProperty);
        set => SetValue(ImageSourceForEdgeColorProperty, value);
    }

    /// <summary>Identifies the ImageSourceForEdgeColor dependency property.</summary>
    public static readonly DependencyProperty ImageSourceForEdgeColorProperty = DependencyProperty.Register(
        nameof(ImageSourceForEdgeColor), typeof(object), typeof(CompositionShadow), new PropertyMetadata(null, OnEdgeColorChanged));

    private static void OnMaskChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((CompositionShadow)sender).UpdateMask();

    private static void OnEdgeColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (CompositionShadow)sender;
        control.RefreshEdgeColor();
        if (!control.UseEdgeColorFromImage || control.ImageSourceForEdgeColor is null) control.Color = Colors.Black;
    }
}
