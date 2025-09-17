using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class Folder : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        private string _name;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        private string _path;
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }
        private string _type;
        public string Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
        }
        private int _songCount;
        public int SongCount
        {
            get => _songCount;
            set => SetProperty(ref _songCount, value);
        }
    }
}
