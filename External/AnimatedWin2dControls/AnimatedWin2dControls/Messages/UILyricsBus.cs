using System;
using System.Collections.Generic;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;

namespace AnimatedWin2dControls.Messages;

public static class UILyricsBus
{
    public static event Action<IList<LyricLine>?>? Changed;
    internal static event Action<IList<LyricLine>?, bool>? PlaybackChanged;

    public static void Publish(IList<LyricLine>? value) => Publish(value, animateChange: false);

    public static void Publish(IList<LyricLine>? value, bool animateChange)
    {
        PlaybackChanged?.Invoke(value, animateChange);
        Changed?.Invoke(value);
    }
}
