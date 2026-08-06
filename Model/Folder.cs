using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class Folder : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name { get => field; set => SetProperty(ref field, value); }
        public string Path { get => field; set => SetProperty(ref field, value); }
        public string Type { get => field; set => SetProperty(ref field, value); }
        public int SongCount { get => field; set => SetProperty(ref field, value); }
    }
}
