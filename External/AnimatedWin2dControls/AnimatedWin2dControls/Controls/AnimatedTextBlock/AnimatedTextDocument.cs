using System;
using System.Collections.Generic;
using Windows.UI.Text;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock;

/// <summary>An immutable, atomic update of independently formatted paragraphs.</summary>
public sealed class AnimatedTextDocument
{
    public IReadOnlyList<AnimatedTextParagraph> Paragraphs { get; }

    public AnimatedTextDocument(params AnimatedTextParagraph[] paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var copy = (AnimatedTextParagraph[])paragraphs.Clone();
        foreach (var paragraph in copy)
            ArgumentNullException.ThrowIfNull(paragraph);
        Paragraphs = Array.AsReadOnly(copy);
    }
}

public sealed class AnimatedTextParagraph
{
    public string Text { get; }
    public double FontSize { get; }
    public FontWeight FontWeight { get; }
    public double LineHeight { get; }
    public double Opacity { get; }

    public AnimatedTextParagraph(string text, double fontSize, FontWeight fontWeight, double lineHeight = 0, double opacity = 1)
    {
        if (!double.IsFinite(fontSize) || fontSize <= 0 || fontSize > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(lineHeight) || lineHeight < 0 || lineHeight > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lineHeight));
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        Text = text ?? string.Empty;
        FontSize = fontSize;
        FontWeight = fontWeight;
        LineHeight = lineHeight;
        Opacity = opacity;
    }
}
