using System;

namespace WinUIMusicPlayer.Taskbar
{
    [Flags]
    public enum ThumbButtonFlags
    {
        Enabled = 0,
        Disabled = 0x1,
        DismissOnClick = 0x2,
        NoBackground = 0x4,
        Hidden = 0x8,
        NonInteractive = 0x10
    }
}
