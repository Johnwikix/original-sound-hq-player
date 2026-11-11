using CommunityToolkit.Mvvm.ComponentModel;

using System.Windows.Input;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Model
{
    public class PlayListMusicItem : ObservableObject
    {
        public Music Music { get; set => SetProperty(ref field, value); }
        /// <summary>获取或设置歌单中本次出现的关系身份。</summary>
        public int EntryId { get; set; }
        /// <summary>获取或设置固定版本，空值表示歌曲选版策略。</summary>
        public int? RequestedMusicId { get; set; }
        /// <summary>获取按本次歌单出现项播放的共享命令。</summary>
        public ICommand PlayCommand => MusicCommands.PlayPlaylistEntryCommand;
        public int PlayListOrder { get; set => SetProperty(ref field, value); } = 0;
    }
}
