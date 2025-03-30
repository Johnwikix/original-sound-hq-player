using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Model
{
    public class OutputDevice
    {
        public string name { get; set; }
        public string id { get; set; }

        public MMDevice mMDevice { get; set; }

        public OutputDevice(string name, string id, MMDevice mMDevice)
        {
            this.name = name;
            this.id = id;
            this.mMDevice = mMDevice;
        }
    }
}