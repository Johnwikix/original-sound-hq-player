using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
