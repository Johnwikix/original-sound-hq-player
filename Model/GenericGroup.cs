using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace WinUIMusicPlayer.Model
{
    /// <summary>
    /// 通用分组模型
    /// TKey: 分组的键（如 A, B, C 或 专辑名）
    /// TItem: 列表项类型（如 Music）
    /// </summary>
    public class GenericGroup
    {
        public string Key { get; set; }
        public ObservableCollection<Music> Items { get; set; } = [];
    }
}
