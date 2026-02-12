using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class PlayList : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name
        {
            get;
            set => SetProperty(ref field, value);
        }
    }
}
