using System;
using System.Threading;

namespace AnimatedWin2dControls.Messages;

public static class TimeProgressBus
{
    public static event Action<long>? CurrentPlayingTimeChanged;
    private static Func<long>? _clock;

    public static void SetClock(Func<long>? clock) => Volatile.Write(ref _clock, clock);

    public static bool TryReadClock(out long currentMs)
    {
        var clock = Volatile.Read(ref _clock);
        currentMs = clock?.Invoke() ?? 0;
        return clock != null;
    }

    public static void Publish(long totalMs) => CurrentPlayingTimeChanged?.Invoke(totalMs);
}
