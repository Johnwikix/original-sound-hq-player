using System;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace WinUIMusicPlayer.Behaviors
{
    public sealed class ScrollerHelper
    {
        private readonly DispatcherQueueTimer _timer;

        public ScrollerHelper(DispatcherQueue queue, TimeSpan? interval = null)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = interval ?? TimeSpan.FromMilliseconds(100);
            _timer.IsRepeating = false;
        }

        public event TypedEventHandler<DispatcherQueueTimer, object> Tick
        {
            add => _timer.Tick += value;
            remove => _timer.Tick -= value;
        }

        public void Trigger() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}
