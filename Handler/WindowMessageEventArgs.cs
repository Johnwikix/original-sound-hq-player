using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Handler
{
    public class WindowMessageEventArgs : EventArgs
    {
        public uint MessageId { get; }
        public IntPtr WParam { get; }
        public IntPtr LParam { get; }

        public WindowMessageEventArgs(uint messageId, IntPtr wParam, IntPtr lParam)
        {
            MessageId = messageId;
            WParam = wParam;
            LParam = lParam;
        }
    }
}
