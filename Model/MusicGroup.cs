using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WinUIMusicPlayer.Model
{
    public class MusicGroup : ObservableCollection<Music>
    {
        public string Key { get; set; }
        public int ItemCount => Count;

        public MusicGroup(string key, IEnumerable<Music> items) : base(items)
        {
            Key = key;
        }
    }
}
