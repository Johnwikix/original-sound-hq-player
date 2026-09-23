using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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
        public int SongCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        /// <summary>按歌单顺序选出的封面曲目；随库和成员映射更新，不持久化图片或曲目副本。</summary>
        [Ignore]
        public Music? CoverMusic { get; set => SetProperty(ref field, value); }

        [RelayCommand]
        public void EnterPlayListView()
        {
            App.Services.GetRequiredService<AppViewModel>().PageType = "playlist";
            App.Services.GetRequiredService<AppViewModel>().CurrentPlayList = this;
            App.Services.GetRequiredService<AppViewModel>().CurrentPlayListId = this.Id;
            AppData.CurrentPage = typeof(PlayListPage);
        }
    }
}
