using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace testDemo.Taskbar
{
    [Flags]
    public enum ThumbButtonMask
    {
        Bitmap = 0x1,
        Icon = 0x2,
        Tooltip = 0x4,
        THB_FLAGS = 0x8
    }
}
