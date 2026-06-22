using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public abstract class BaseRenderLyrics
    {
        public double StartMs { get; set; }
        public double? EndMs { get; set; }
        public double DurationMs => Math.Max((EndMs ?? 0) - StartMs, 0);

        public string Text { get; set; } = "";
        public int StartIndex { get; set; }
        public int EndIndex => StartIndex + Text.Length - 1;

        public bool IsPlayingLastFrame { get; set; }

        public bool GetIsPlaying(double currentMs) => StartMs <= currentMs && currentMs < EndMs;
        public double GetPlayProgress(double currentMs) => Math.Clamp((currentMs - StartMs) / DurationMs, 0, 1);
    }
}
