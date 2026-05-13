using System.Collections.Generic;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public class RenderLyricsSyllable : BaseRenderLyrics
    {
        public List<RenderLyricsChar> ChildrenRenderLyricsChars { get; set; } = [];

        public int Length => Text.Length;

        public RenderLyricsSyllable() { }
    }
}
