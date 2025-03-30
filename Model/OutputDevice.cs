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
        public MMDevice mMDevice { get; set; }

        public OutputDevice(MMDevice mMDevice)
        {
            this.mMDevice = mMDevice;
        }
    }
}