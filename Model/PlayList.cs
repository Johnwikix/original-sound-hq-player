using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class PlayList : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        private string _name;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }
}
