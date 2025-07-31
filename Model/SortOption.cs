using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Model
{
    public class SortOption : ObservableObject
    {
        private string _displayText;

        public string Tag { get; set; }
        public string UidKey { get; set; }

        public string DisplayText
        {
            get => _displayText;
            set => SetProperty(ref _displayText, value);
        }

        public SortOption(string Tag, string UidKey) {
            this.Tag = Tag;
            this.UidKey = UidKey;
            this.DisplayText = ToolUtils.GetString(UidKey);
        }

    }
}
