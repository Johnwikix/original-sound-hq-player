using System;

namespace AnimatedWin2dControls.Messages;

public static class TimeProgressBus
{
    public static event Action<long>? CurrentPlayingTimeChanged;

    public static void Publish(long totalMs) => CurrentPlayingTimeChanged?.Invoke(totalMs);
}
