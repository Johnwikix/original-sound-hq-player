using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Model
{
    public class UsbStorageDevice
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public double FreeSpaceInGB { get; set; }
        public double TotalSpaceInGB { get; set; }
        public string UniqueId { get; set; } // 添加唯一ID属性
    }
}
