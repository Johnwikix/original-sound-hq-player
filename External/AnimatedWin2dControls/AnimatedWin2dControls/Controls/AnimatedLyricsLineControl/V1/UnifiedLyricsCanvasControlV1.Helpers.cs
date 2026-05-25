using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Text;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;

public sealed partial class UnifiedLyricsCanvasControlV1
{
    private void InvalidateLayoutCache()
    {
        _cachedLayoutKey = null;
        _cachedLayoutWidth = 0f;
        _lineLayoutCount = 0;
        _totalWordCount = 0;
        _visualRowCount = 0;
        _rowCurveCount = 0;
        DisposeAllClearCache();
        _cacheWindowLo = -1;
        _cacheWindowHi = -1;
        ResetSmoothedRevealX();
        DisposeFmtCache();
    }

    private void DisposeAllClearCache()
    {
        foreach (var rt in _clearLineCache) rt?.Dispose();
        _clearLineCache = [];
        _blurAlpha = [];
    }

    private void ResetSmoothedRevealX()
    {
        for (int i = 0; i < _smoothedRevealX.Length; i++)
            _smoothedRevealX[i] = float.NaN;
    }

    private void EnsureSmoothedRevealXCapacity(int count)
    {
        if (_smoothedRevealX.Length >= count) return;
        _smoothedRevealX = new float[count + 4];
        for (int i = 0; i < _smoothedRevealX.Length; i++)
            _smoothedRevealX[i] = float.NaN;
    }

    private void DisposeFmtCache()
    {
        _lyricsFmt?.Dispose(); _lyricsFmt = null;
        _transFmt?.Dispose(); _transFmt = null;
        _cachedFontFamily = null;
    }

    private CanvasTextFormat GetLyricsFmt(float dpi)
    {
        float sz = (float)Math.Round(_cachedLyricsFontSize * 96f / dpi);
        string fam = _cachedFontFamilyName;
        if (_lyricsFmt is null || _cachedFontFamily != fam || _cachedLyricsFontSizeForFmt != sz)
        {
            _lyricsFmt?.Dispose();
            _lyricsFmt = new CanvasTextFormat
            {
                FontFamily = fam,
                FontSize = sz,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };
            _cachedFontFamily = fam;
            _cachedLyricsFontSizeForFmt = sz;
        }
        return _lyricsFmt;
    }

    private CanvasTextFormat GetTransFmt(float dpi)
    {
        float sz = (float)Math.Round(_cachedLyricsFontSize * 0.75f * 96f / dpi);
        string fam = _cachedFontFamilyName;
        var align = _cachedLyricsTextAlignment;
        if (_transFmt is null || _cachedFontFamily != fam ||
            _cachedTransFontSizeForFmt != sz || _cachedTransAlignmentForFmt != align)
        {
            _transFmt?.Dispose();
            _transFmt = new CanvasTextFormat
            {
                FontFamily = fam,
                FontSize = sz,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = align,
            };
            _cachedTransFontSizeForFmt = sz;
            _cachedTransAlignmentForFmt = align;
            _cachedFontFamily = fam;
        }
        return _transFmt;
    }

    private void UpdateColors(bool isDark)
    {
        byte unplayedAlpha = (byte)(_cachedUnplayedOpacity * 255);
        byte translateAlpha = (byte)(_cachedTranslatedOpacity * 255);
        if (isDark)
        {
            _dimColor = Color.FromArgb(unplayedAlpha, 255, 255, 255);
            _brightColor = Color.FromArgb(255, 255, 255, 255);
            _translateColor = Color.FromArgb(translateAlpha, 255, 255, 255);
        }
        else
        {
            _dimColor = Color.FromArgb(unplayedAlpha, 0, 0, 0);
            _brightColor = Color.FromArgb(255, 0, 0, 0);
            _translateColor = Color.FromArgb(translateAlpha, 0, 0, 0);
        }
        _gradBrushDirty = true;
    }

    private static string BuildLayoutKey(IList<LyricLine> lyrics)
    {
        var sb = new StringBuilder(lyrics.Count * 8);
        foreach (var line in lyrics)
            sb.Append(line.Words.Count).Append('|')
              .Append(line.TransLateText?.Length ?? 0).Append(';');
        return sb.ToString();
    }

    private static string BuildFullText(IList<LyricWord> words)
    {
        var sb = new StringBuilder(words.Count * 6);
        foreach (var w in words) sb.Append(w.Word);
        return sb.ToString();
    }
}
