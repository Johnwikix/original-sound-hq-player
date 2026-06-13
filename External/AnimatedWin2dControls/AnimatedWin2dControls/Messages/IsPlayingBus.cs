using System;

namespace AnimatedWin2dControls.Messages;

public static class IsPlayingBus
{
    public static event Action<bool>? Changed;
    public static void Publish(bool value) => Changed?.Invoke(value);
}
