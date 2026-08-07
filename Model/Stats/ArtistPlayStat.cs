using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Pages;

namespace WinUIMusicPlayer.Model.Stats
{
    using WinUIMusicPlayer.Model;

    /// <summary>
    /// 单个歌手/艺术家的汇总统计（达标收听次数与累计收听秒数）。
    /// </summary>
    public partial class ArtistPlayStat : ObservableObject
    {
        public string Artist { get; init; } = string.Empty;

        /// <summary>该组最近一次会话的歌曲 Id（封面与播放锚点，歌曲可能已删除）。</summary>
        public int MusicId { get; init; }

        /// <summary>由 <see cref="MusicId"/> 解析出的歌曲对象（删除后为 null）。</summary>
        public Music? Music { get; set; }

        /// <summary>达标（听满阈值比例）次数。</summary>
        public int PlayCount { get => field; set => SetProperty(ref field, value); }

        /// <summary>累计收听秒数。</summary>
        public double TotalDurationSeconds { get => field; set => SetProperty(ref field, value); }

        /// <summary>点击封面播放该歌手全部歌曲（x:Bind 编译期绑定，trim 安全）。</summary>
        [RelayCommand]
        private async Task Play()
        {
            var services = App.Services;
            var app = services.GetRequiredService<AppViewModel>();

            var src = app.SongsSource;
            var list = new List<Music>(Math.Max(src.Count, 1));
            for (int i = 0; i < src.Count; i++)
            {
                var m = src[i];
                if (m.Author is not null && m.Author.Equals(Artist, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(m);
                }
            }
            if (list.Count == 0) return;

            list.Sort((a, b) => string.CompareOrdinal(a.Album, b.Album));
            app.SequentialPlayingList = new BulkObservableCollection<Music>(list);
            await services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(list[0], IsChangeList: true);
        }
    }
}
