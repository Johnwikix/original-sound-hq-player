using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms.Design;

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static OutputDevice OutputDevice { get; set; } = new OutputDevice("Defualt",-1);
        public static string OutputMode { get; set; } = "WasapiExclusive";
        public static int latency { get; set; } = 200;
    }
}
