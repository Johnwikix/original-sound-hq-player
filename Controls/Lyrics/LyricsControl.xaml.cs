using DevWinUI;
using Lyricify.Lyrics.Providers.Web.Netease;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using WinUIMusicPlayer.Model;
using ZLinq;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class LyricsControl : UserControl
    {
        public event EventHandler<TimeSpan> LyricInteracted;
        // 依赖属性
        public static readonly DependencyProperty UILyricsProperty =
            DependencyProperty.Register(
                nameof(UILyrics),
                typeof(ObservableCollection<LyricLine>),
                typeof(LyricsControl),
                new PropertyMetadata(null));

        public ObservableCollection<LyricLine> UILyrics
        {
            get => (ObservableCollection<LyricLine>)GetValue(UILyricsProperty);
            set => SetValue(UILyricsProperty, value);
        }

        public LyricsControl()
        {
            this.InitializeComponent();
        }

        private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LyricLine lyricLine)
            {
                LyricInteracted?.Invoke(this, lyricLine.Time);
            }
        }

        private void LyricsLineGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var blurControl = grid?.Children.AsValueEnumerable()
                          .OfType<BlurEffectControl>()
                          .FirstOrDefault();
                blurControl?.GetBlurEffectManager()?.StartBlurReverseAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(350));
                if (AppSettings.LyricsBlurAmount < 1)
                {
                    if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out var resourceValue))
                    {
                        var secondaryBrush = resourceValue as SolidColorBrush;
                        grid?.Background = secondaryBrush ?? new(Color.FromArgb(25, 255, 255, 255));
                    }
                }
            }
        }

        private void LyricsLineGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var blurControl = grid?.Children.AsValueEnumerable()
                        .OfType<BlurEffectControl>()
                        .FirstOrDefault();
                blurControl?.GetBlurEffectManager()?.StartBlurAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(350));
                if (AppSettings.LyricsBlurAmount < 1)
                {
                    grid?.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
            }
        }
    }
}
