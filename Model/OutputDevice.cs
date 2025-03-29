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
        public int ID { get; set; }

        public OutputDevice(string name, int id)
        {
            this.name = name;
            this.ID = id;
        }
    }
}