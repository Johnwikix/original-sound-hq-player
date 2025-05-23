using System;

namespace testDemo.Taskbar
{
    public class ThumbButtonClickedEventArgs : EventArgs
    {
        public int ButtonId { get; }

        public ThumbButtonClickedEventArgs(int buttonId)
        {
            ButtonId = buttonId;
        }
    }
}
