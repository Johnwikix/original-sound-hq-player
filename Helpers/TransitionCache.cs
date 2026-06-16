using Microsoft.UI.Xaml;
using System;

namespace WinUIMusicPlayer.Helpers
{
    public static class TransitionCache
    {
        public static readonly ScalarTransition Fast = new() { Duration = TimeSpan.FromMilliseconds(100) };
        public static readonly ScalarTransition Slow = new() { Duration = TimeSpan.FromMilliseconds(300) };
        public static readonly ScalarTransition Default = new() { Duration = Constants.Time.AnimationDuration };
    }
}