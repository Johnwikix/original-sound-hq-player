using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Pages;

namespace WinUIMusicPlayer.Model.Stats
{
    using WinUIMusicPlayer.Model;

    /// <summary>
    /// 单首歌曲的汇总统计（达标收听次数与累计收听秒数）。
    /// </summary>
    public partial class SongPlayStat : ObservableObject
    {
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string Album { get; init; } = string.Empty;

        /// <summary>该组最近一次会话的歌曲 Id（封面与播放锚点，歌曲可能已删除）。</summary>
        public int MusicId { get; init; }

        /// <summary>由 <see cref="MusicId"/> 解析出的歌曲对象（删除后为 null）。</summary>
        public Music? Music { get; set; }

        /// <summary>达标（听满阈值比例）次数。</summary>
        public int PlayCount { get => field; set => SetProperty(ref field, value); }

        /// <summary>累计收听秒数。</summary>
        public double TotalDurationSeconds { get => field; set => SetProperty(ref field, value); }

        /// <summary>点击封面播放（x:Bind 编译期绑定，trim 安全）。</summary>
        [RelayCommand]
        private async Task Play()
        {
            if (Music is null) return;

            var services = App.Services;
            var app = services.GetRequiredService<AppViewModel>();
            var stats = services.GetRequiredService<StatsViewModel>();

            var queue = new List<Music>(stats.TopSongs.Count);
            foreach (var song in stats.TopSongs)
            {
                if (song.Music is not null) queue.Add(song.Music);
            }
            if (queue.Count == 0) return;

            app.SequentialPlayingList = new BulkObservableCollection<Music>(queue);
            await services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(Music, IsChangeList: true);
        }
    }
}
