using System;
using System.Collections.Generic;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;

namespace AnimatedWin2dControls.Messages;

public static class UILyricsBus
{
    public static event Action<IList<LyricLine>?>? Changed;
    public static void Publish(IList<LyricLine>? value) => Changed?.Invoke(value);
}
