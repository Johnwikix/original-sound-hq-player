using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimatedWin2dControls.Utils
{
    public class ToolUtils
    {
        public static int ComputeFastHash(byte[] b)
        {
            int len = b.Length;
            var hc = new HashCode();
            hc.Add(b[0]);
            hc.Add(b[len / 4]);
            hc.Add(b[len / 2]);
            hc.Add(b[len * 3 / 4]);
            hc.Add(b[len - 1]);
            hc.Add(len);
            return hc.ToHashCode();
        }
    }
}
