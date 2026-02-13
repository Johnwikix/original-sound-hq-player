using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Media.Animation;
using SQLite;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Model
{
    public partial class PlayList : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name
        {
            get;
            set => SetProperty(ref field, value);
        }

        [RelayCommand]
        public void EnterPlayListView()
        {
            App.Services.GetRequiredService<AppViewModel>().PageType = "playlist";
            App.Services.GetRequiredService<AppViewModel>().CurrentPlayList = this;
            App.Services.GetRequiredService<AppViewModel>().CurrentPlayListId = this.Id;
            AppData.CurrentPage = typeof(PlayListSongPage);
            App.Services.GetRequiredService<MusicBrowsePage>().NavigatePage(AppData.CurrentPage, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
        }
    }
}
