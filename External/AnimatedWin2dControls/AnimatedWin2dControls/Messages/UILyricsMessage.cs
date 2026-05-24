using System.Collections.Generic;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;

namespace AnimatedWin2dControls.Messages;

public sealed record UILyricsMessage(IList<LyricLine>? Lines);
