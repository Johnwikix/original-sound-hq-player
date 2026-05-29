using System;

namespace AnimatedWin2dControls.Controls
{
    public interface ISharedTickable
    {
        void OnSharedTick(TimeSpan elapsed);
    }
}
