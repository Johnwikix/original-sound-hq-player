using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimatedWin2dControls.Controls
{
    public interface ISharedTickable
    {
        void OnSharedTick(TimeSpan elapsed);
    }
}
