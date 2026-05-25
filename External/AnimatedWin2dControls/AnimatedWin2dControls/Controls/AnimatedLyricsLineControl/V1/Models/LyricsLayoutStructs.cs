namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V1;

internal struct WordLayout
{
    public string Text;
    public float X, Y, FullWidth, Height;
}

internal struct LineLayout
{
    public int WordStart;
    public int WordCount;
    public float OffsetY;
    public float Height;
    public float TranslateOffsetY;
    public bool HasTranslate;
}

internal struct VisualRow
{
    public float MinX, MaxX, Y, H;
    public int WordStart, WordEnd;
}

internal struct LineVisualRowRange
{
    public int Start, Count;
}
