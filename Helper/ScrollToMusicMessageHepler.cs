using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    public class ScrollToMusicMessageHepler
    {
        public Music SelectedMusic { get; }

        public ScrollToMusicMessageHepler(Music selectedMusic)
        {
            SelectedMusic = selectedMusic;
        }
    }
}
