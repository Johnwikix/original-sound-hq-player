using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;

namespace WinUIMusicPlayer.Model
{
    public class MenuModel
    {
        public string Title { get; set; }     // 对应 x:Uid 或 Text
        public string Glyph { get; set; }     // 图标（可选）
        public object Tag { get; set; }       // 存放类似 "wav", "mp3" 的参数
        public ICommand Command { get; set; } // 绑定的事件
        public IEnumerable<MenuModel> Children { get; set; } // 子菜单
    }
}
