using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Parser
{
    public class Id3v2ParseResult
    {
        public Dictionary<string,string> TextTags { get; set; }
        public List<Id3v2Picture> Pictures { get; set; }
        public byte MajorVersion { get; set; }
        public byte MinorVersion { get; set; }
        public int TagSize { get; set; }
}
}
