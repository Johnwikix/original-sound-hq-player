using System;

namespace WinUIMusicPlayer.Services;

[Flags]
internal enum FftConsumer
{
    Taskbar = 1,
    PlaybackSpectrum = 2
}
