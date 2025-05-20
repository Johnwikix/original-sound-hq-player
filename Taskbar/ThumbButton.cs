using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace testDemo.Taskbar
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct ThumbButton
    {
        public ThumbButtonMask dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public ThumbButtonFlags dwFlags;
    }
}
