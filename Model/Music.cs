using AnimatedWin2dControls.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLite;
using System;
using System.Windows.Input;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Model
{
    public partial class Music : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Path { get; set => SetProperty(ref field, value); } = string.Empty;
        public string Title { get; set => SetProperty(ref field, value); } = string.Empty;
        public string Author { get; set => SetProperty(ref field, value); } = string.Empty;
        public TimeSpan Duration { get; set; }
        public string Album { get; set => SetProperty(ref field, value); } = string.Empty;

        public string FolderPath { get; set; } = string.Empty;
        public string LastLevelFolderPath { get; set => SetProperty(ref field, value); } = string.Empty;
        public string Extension { get; set => SetProperty(ref field, value); } = string.Empty;
        public int Order { get; set => SetProperty(ref field, value); } = 0;
        public int BitDepth { get; set => SetProperty(ref field, value); } = 0;
        public int BitRate { get; set => SetProperty(ref field, value); } = 0;
        public int SampleRate { get; set => SetProperty(ref field, value); }
        public int Channel { get; set => SetProperty(ref field, value); } = 0;
        public int Year { get; set => SetProperty(ref field, value); } = 0;
        public bool IsFavorite { get; set => SetProperty(ref field, value); } = false;
        public int TrackNumber { get; set => SetProperty(ref field, value); } = 0;
        public int DiskNumber { get; set => SetProperty(ref field, value); } = 0;
        public int LyricsOffsetMs
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    MusicCommands.OnLyricsOffsetChanged(this, value);
                }
            }
        } = 0;
        public int PlayCount { get; set; } = 0;
        public bool IsLrcSearched { get; set; } = false;
        public bool IsKrcSearched { get; set; } = false;
        [Ignore]
        public int IsExistOnDevice { get; set => SetProperty(ref field, value); } = 0;
        public string ImageHash { get; set => SetProperty(ref field, value); } = string.Empty;
        public DateTime CreateTime { get; set => SetProperty(ref field, value); }
        public DateTime UpdateTime { get; set => SetProperty(ref field, value); }

        [Ignore] public ICommand PlayCommand => MusicCommands.PlayCommand;
        [Ignore] public ICommand UpdateFavouriteCommand => MusicCommands.UpdateFavouriteCommand;
        [Ignore] public ICommand AddMusicToCurrentPlayListCommand => MusicCommands.AddToPlayListCommand;
    }
}
