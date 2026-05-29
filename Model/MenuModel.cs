using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace WinUIMusicPlayer.Model
{
    public class MenuModel : ObservableObject
    {
        public string Title { get; set => SetProperty(ref field, value); }
        public string Glyph { get; set => SetProperty(ref field, value); }
        public object Tag { get; set => SetProperty(ref field, value); }
        public ICommand Command { get; set; } // 绑定的事件
        public ObservableCollection<MenuModel> Children { get; set => SetProperty(ref field, value); }
    }
}
