using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class Folder:ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string _name;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        public string _path;
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }
        public string _type;
        public string Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
        }
    }
}
