using System.Collections.Generic;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public class RenderLyricsSyllable : BaseRenderLyrics
    {
        public List<RenderLyricsChar> ChildrenRenderLyricsChars { get; set; } = [];

        public int Length => Text.Length;

        public RenderLyricsSyllable() { }
    }
}
