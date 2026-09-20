using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;
using System;
using System.Windows.Input;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Model
{
    public class Folder : ObservableObject
    {
        /// <summary>本地扫描根的类型值（存量数据字面量，保持不变）。</summary>
        public const string TypeLocal = "本地";
        /// <summary>外部导入虚拟文件夹的类型值：归集管理双击打开入库的固定盘文件。</summary>
        public const string TypeExternal = "external";
        /// <summary>虚拟行哨兵路径：非磁盘路径格式，永不与真实目录产生 IsWithin 包含关系。</summary>
        public const string ExternalPath = "external://imports";

        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name { get => field; set => SetProperty(ref field, value); }
        public string Path { get => field; set => SetProperty(ref field, value); }
        public string Type { get => field; set => SetProperty(ref field, value); }
        public int SongCount { get => field; set => SetProperty(ref field, value); }

        /// <summary>是否外部导入虚拟行：Path 为哨兵，不可枚举/监视/在资源管理器打开；
        /// 其成员 = 路径不在任何本地扫描根内的 Music 行（归属按路径现算，不落标记）。</summary>
        public bool IsExternalImport => string.Equals(Type, TypeExternal, StringComparison.Ordinal);

        /// <summary>是否提供"打开所在位置"入口（虚拟行无对应磁盘目录）。</summary>
        public bool CanOpenInExplorer => !IsExternalImport;

        // 与 Music.PlayCommand 同模式：DataTemplate 内 x:Bind 需要数据项上的命令入口
        [Ignore]
        public ICommand OpenFolderCommand => FolderCommands.OpenFolderCommand;
        [Ignore]
        public ICommand RescanFolderCommand => FolderCommands.RescanFolderCommand;
        [Ignore]
        public ICommand RemoveFolderCommand => FolderCommands.RemoveFolderCommand;
    }
}
