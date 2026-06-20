using Microsoft.UI.Xaml.Data;
using System;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Converters
{
    public partial class PlayModeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is PlayMode playMode)
            {
                switch (playMode)
                {
                    case PlayMode.SingleLoop:
                        return "\ue8ed";
                    case PlayMode.ListLoop:
                        return "\ue8ee";
                    case PlayMode.RandomLoop:
                        return "\ue8b1";
                    case PlayMode.RepeatOff:
                        return "\uF5E7";
                    default:
                        return "\ue8ee"; // 默认问号图标
                }
            }
            return "\ue8ee";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            //throw new NotImplementedException();
            if (value is string iconString)
            {
                switch (iconString)
                {
                    case "\ue8ed":
                        return PlayMode.SingleLoop;
                    case "\ue8ee":
                        return PlayMode.ListLoop;
                    case "\ue8b1":
                        return PlayMode.RandomLoop;
                    case "\uF5E7":
                        return PlayMode.RepeatOff;
                    default:
                        return PlayMode.ListLoop;
                }
            }
            return PlayMode.ListLoop;
        }
    }
}
