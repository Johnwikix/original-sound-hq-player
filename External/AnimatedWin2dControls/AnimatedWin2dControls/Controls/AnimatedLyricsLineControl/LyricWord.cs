using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    public class LyricWord
    {
        public string Word { get; set; } = string.Empty;
        public double StartMs { get; set; }
        public double DurationMs { get; set; }
    }
}
