using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using System.Collections.Generic;

namespace AnimatedWin2dControls.Messages;

public sealed record UILyricsMessage(IList<LyricLine>? Lines);
