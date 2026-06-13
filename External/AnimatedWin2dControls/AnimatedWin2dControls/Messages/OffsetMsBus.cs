using System;

namespace AnimatedWin2dControls.Messages;

public static class OffsetMsBus
{
    public static event Action<double>? Changed;
    public static void Publish(double value) => Changed?.Invoke(value);
}
