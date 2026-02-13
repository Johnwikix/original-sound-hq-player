using DevWinUI;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using ZLinq;

namespace WinUIMusicPlayer.View
{
    public sealed partial class MusicBrowsePage
    {
        private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LyricLine lyricLine)
            {
                Task.Run(() =>
                {
                    int index = ViewModel.AppViewModel.UILyrics.IndexOf(ViewModel.AppViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.Time >= lyricLine.Time));                    
                    ViewModel.isManualSelect = true;
                    ViewModel.UpdateLyricsToUI(index);
                    ViewModel._musicPlaybackService.ChangeWaveChannelTime(lyricLine.Time);                    
                    ViewModel.isManualSelect = false;
                });
            }
        }

        private void LyricsLineGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid )
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
                if (AppSettings.LyricsBlurAmount<1)
                {
                    grid?.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
            }
        }
    }
}
