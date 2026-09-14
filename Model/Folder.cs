using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;
using System.Windows.Input;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Model
{
    public class Folder : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name { get => field; set => SetProperty(ref field, value); }
        public string Path { get => field; set => SetProperty(ref field, value); }
        public string Type { get => field; set => SetProperty(ref field, value); }
        public int SongCount { get => field; set => SetProperty(ref field, value); }

        // 与 Music.PlayCommand 同模式：DataTemplate 内 x:Bind 需要数据项上的命令入口
        [Ignore]
        public ICommand OpenFolderCommand => FolderCommands.OpenFolderCommand;
        [Ignore]
        public ICommand RescanFolderCommand => FolderCommands.RescanFolderCommand;
        [Ignore]
        public ICommand RemoveFolderCommand => FolderCommands.RemoveFolderCommand;
    }
}
