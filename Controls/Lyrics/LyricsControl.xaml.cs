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

        public static readonly DependencyProperty LyricsMarginProperty = DependencyProperty.Register(
            nameof(LyricsMargin),
            typeof(Thickness),
            typeof(LyricsControl),
            new PropertyMetadata(new Thickness(0)));

        public Thickness LyricsMargin
        {
            get => (Thickness)GetValue(LyricsMarginProperty);
            set => SetValue(LyricsMarginProperty, value);
        }


        // ── 新增：当前播放时间（外部 200ms 轮询写入）──────────────────────
        public static readonly DependencyProperty CurrentPlayingTimeProperty =
            DependencyProperty.Register(
                nameof(CurrentPlayingTime),
                typeof(TimeSpan),
                typeof(LyricsControl),
                new PropertyMetadata(TimeSpan.Zero, OnCurrentPlayingTimeChanged));

        public TimeSpan CurrentPlayingTime
        {
            get => (TimeSpan)GetValue(CurrentPlayingTimeProperty);
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        private static void OnCurrentPlayingTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LyricsControl ctrl)
            {
                var externalTime = (TimeSpan)e.NewValue;
                var diff = (externalTime - ctrl._internalPosition).Duration(); // 取绝对值

                // 误差超过 50ms 才强制校准，避免外部轮询抖动干扰内部节拍
                if (diff > TimeSpan.FromMilliseconds(100))
                    ctrl._internalPosition = externalTime;
            }
        }

        // ── 新增：是否正在播放──────────────────────────────────────────
        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(
                nameof(IsPlaying),
                typeof(bool),
                typeof(LyricsControl),
                new PropertyMetadata(false, OnIsPlayingChanged));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LyricsControl ctrl)
            {
                if ((bool)e.NewValue)
                    ctrl.StartInternalTimer();
                else
                    ctrl.StopInternalTimer();
            }
        }


        // ── 内部高频计时器（~16.67ms / 60fps）─────────────────────────
        private DispatcherTimer _internalTimer;
        private TimeSpan _internalPosition = TimeSpan.Zero;
        private DateTime _lastTickTime;
        private int _lastLyricIndex = -1;

        private void StartInternalTimer()
        {
            if (_internalTimer.IsEnabled) return;
            _lastTickTime = DateTime.UtcNow;
            _internalTimer.Start();
        }

        private void StopInternalTimer()
        {
            _internalTimer.Stop();
        }

        private void InternalTimer_Tick(object sender, object e)
        {
            // 用真实墙钟时间推进，避免 Timer 精度抖动累积误差
            var now = DateTime.UtcNow;
            var elapsed = now - _lastTickTime;
            _lastTickTime = now;

            _internalPosition += elapsed;
            UpdateLyricsInternal(_internalPosition);
            var lyrics = UILyrics;
            if (lyrics != null && _lastLyricIndex >= 0 && _lastLyricIndex < lyrics.Count)
            {
                lyrics[_lastLyricIndex].CurrentPlayingTime = _internalPosition;
            }
        }

        // ── 歌词匹配逻辑（从外部移入控件）────────────────────────────────

        private void UpdateLyricsInternal(TimeSpan position)
        {
            var lyrics = UILyrics;
            if (lyrics == null || lyrics.Count == 0) return;

            int currentIndex = -1;
            for (int i = 0; i < lyrics.Count; i++)
            {
                if (lyrics[i].Time <= position)
                    currentIndex = i;
                else
                    break;
            }

            if (currentIndex < 0 || currentIndex == _lastLyricIndex) return;

            // O(1) 切换高亮，不遍历全部
            int prev = _lastLyricIndex;
            _lastLyricIndex = currentIndex;

            if (prev >= 0 && prev < lyrics.Count)
                lyrics[prev].IsCurrent = false;

            lyrics[currentIndex].IsCurrent = true;
        }

        public LyricsControl()
        {
            this.InitializeComponent();
            _internalTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16.67) // ~60fps
            };
            _internalTimer.Tick += InternalTimer_Tick;

            this.Unloaded += (_, _) => StopInternalTimer();
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
                //var blurControl = grid?.Children.AsValueEnumerable()
                //          .OfType<BlurEffectControl>()
                //          .FirstOrDefault();
                //blurControl?.GetBlurEffectManager()?.StartBlurReverseAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(350));
                //if (AppSettings.LyricsBlurAmount < 1)
                //{
                //    if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out var resourceValue))
                //    {
                //        var secondaryBrush = resourceValue as SolidColorBrush;
                //        grid?.Background = secondaryBrush ?? new(Color.FromArgb(25, 255, 255, 255));
                //    }
                //}
                if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out var resourceValue))
                {
                    var secondaryBrush = resourceValue as SolidColorBrush;
                    grid?.Background = secondaryBrush ?? new(Color.FromArgb(25, 255, 255, 255));
                }
            }
        }

        private void LyricsLineGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                //var blurControl = grid?.Children.AsValueEnumerable()
                //        .OfType<BlurEffectControl>()
                //        .FirstOrDefault();
                //blurControl?.GetBlurEffectManager()?.StartBlurAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(350));
                //if (AppSettings.LyricsBlurAmount < 1)
                //{
                //    grid?.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                //}
                grid?.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
    }
}
