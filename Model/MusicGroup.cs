using System.Collections.Generic;

namespace WinUIMusicPlayer.Model
{
    public class MusicGroup : List<Music>
    {
        public string Key { get; set; }
        public int ItemCount => Count;

        public MusicGroup(string key, IEnumerable<Music> items) : base(items)
        {
            Key = key;
        }
    }
}
