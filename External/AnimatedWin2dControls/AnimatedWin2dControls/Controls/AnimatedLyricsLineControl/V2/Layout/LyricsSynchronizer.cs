using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public class LyricsSynchronizer
    {
        private int _lastFoundIndex;

        public void Reset() => _lastFoundIndex = 0;

        public int GetCurrentLineIndex(double currentTimeMs, System.Collections.Generic.IList<RenderLyricsLine>? lines)
        {
            if (lines == null || lines.Count == 0) return 0;

            if (_lastFoundIndex >= 0 && _lastFoundIndex < lines.Count)
            {
                if (IsTimeInLine(currentTimeMs, lines, _lastFoundIndex))
                    return _lastFoundIndex;
            }

            int bestCandidateIndex = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                if (IsTimeInLine(currentTimeMs, lines, i))
                {
                    _lastFoundIndex = i;
                    return i;
                }
                else if (lines[i].StartMs > currentTimeMs + 1000)
                {
                    break;
                }
            }

            if (bestCandidateIndex != -1)
            {
                _lastFoundIndex = bestCandidateIndex;
                return bestCandidateIndex;
            }

            return Math.Min(_lastFoundIndex, lines.Count - 1);
        }

        private static bool IsTimeInLine(double time, System.Collections.Generic.IList<RenderLyricsLine> lines, int index)
        {
            if (index < 0 || index >= lines.Count) return false;
            var line = lines[index];
            if (time < line.StartMs) return false;
            if (index + 1 < lines.Count && time >= lines[index + 1].StartMs) return false;
            return true;
        }
    }
}
