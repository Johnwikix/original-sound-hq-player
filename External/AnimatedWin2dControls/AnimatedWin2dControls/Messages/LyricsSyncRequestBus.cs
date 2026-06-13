using System;

namespace AnimatedWin2dControls.Messages;

public static class LyricsSyncRequestBus
{
    public static event Action? Requested;
    public static void Request() => Requested?.Invoke();
}
