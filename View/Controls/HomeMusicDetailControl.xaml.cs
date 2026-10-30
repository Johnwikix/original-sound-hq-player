using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>编辑首页歌曲的标签、歌词与封面，复用现有草稿、保存管线和首页命令。</summary>
public sealed partial class HomeMusicDetailControl : UserControl
{
    /// <summary>标识详情曲目。</summary>
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(HomeSong), typeof(HomeMusicDetailControl), new PropertyMetadata(null, SongChanged));
    /// <summary>标识首页命令和歌单菜单来源。</summary>
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(HomeViewModel), typeof(HomeMusicDetailControl), new PropertyMetadata(null));
    /// <summary>标识独立的标签、歌词和封面编辑会话。</summary>
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(MusicDetailsEditorViewModel), typeof(HomeMusicDetailControl), new PropertyMetadata(null, EditorChanged));
    /// <summary>获取或设置当前编辑会话。</summary>
    public MusicDetailsEditorViewModel? Editor { get => (MusicDetailsEditorViewModel?)GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    /// <summary>获取或设置当前规范曲目。</summary>
    public HomeSong? Song { get => (HomeSong?)GetValue(SongProperty); set => SetValue(SongProperty, value); }
    /// <summary>获取或设置首页状态与命令。</summary>
    public HomeViewModel? ViewModel { get => (HomeViewModel?)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    /// <summary>获取共享封面动画的目的元素。</summary>
    public FrameworkElement CoverElement => CoverHost;
    /// <summary>获取与封面协调进入的文本与按钮。</summary>
    internal UIElement[] CoordinatedElements { get; }

    /// <summary>初始化属性视图，布局变化只调整视觉排列。</summary>
    public HomeMusicDetailControl()
    {
        InitializeComponent();
        CoordinatedElements = [HeroText];
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
    }
    private static void SongChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (HomeMusicDetailControl)sender;
        control.PlaylistMenu.Hide();
        control.Artwork.Music = control.Song?.Music;
        if (control.Song is null)
        {
            control.PlayButton.CommandParameter = null;
            control.FavoriteButton.CommandParameter = null;
            control.QueueButton.CommandParameter = null;
            control.CoverPreviewImage.Source = null;
        }
        control.DetailScroll.ChangeView(null, 0, null, true);
    }
    private static void EditorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not null) return;
        var control = (HomeMusicDetailControl)sender;
        control.CoverPreviewImage.Source = null;
        control.ActivityIndicator.IsActive = false;
        control.DismissMenus();
    }
    private void Confirm_Click(object sender, RoutedEventArgs args) => ConfirmFlyout.Hide();
    private void Cancel_Click(object sender, RoutedEventArgs args) => ConfirmFlyout.Hide();
    private void OnUnloaded(object sender, RoutedEventArgs args) => DismissMenus();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool compact = ActualWidth < 760;
        CoverHost.Width = CoverHost.Height = compact ? 200 : 256;
        Grid.SetRow(HeroText, compact ? 1 : 0);
        Grid.SetColumn(HeroText, compact ? 0 : 1);
        Grid.SetColumnSpan(HeroText, compact ? 2 : 1);
    }
    /// <summary>收起详情关联的独立菜单。</summary>
    public void DismissMenus()
    {
        PlaylistMenu.Hide();
        ConfirmFlyout.Hide();
    }
    /// <summary>聚焦固定标题，避免禁用的播放按钮使焦点自动滚入下面的输入区。</summary>
    public void FocusHeader() => HeaderFocus.Focus(FocusState.Programmatic);
    /// <summary>判断属性面板是否需要显示共享命令的错误反馈。</summary>
    public bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
    /// <summary>只在有内容时显示歌词状态。</summary>
    public Visibility TextVisibility(string? value) => HasText(value) ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>展示缺失文本的占位。</summary>
    public string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    /// <summary>展示有效的整数属性。</summary>
    public string Number(int value) => value > 0 ? value.ToString(CultureInfo.CurrentCulture) : "—";
    /// <summary>展示包含小时的完整时长。</summary>
    public string Duration(TimeSpan value) => value <= TimeSpan.Zero ? "—" : value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : value.ToString(@"mm\:ss", CultureInfo.CurrentCulture);
    /// <summary>展示文件创建与更新时间。</summary>
    public string Time(DateTime value) => value == default ? "—" : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    /// <summary>展示位深及单位。</summary>
    public string BitDepth(int value) => value > 0 ? $"{value} bit" : "—";
    /// <summary>展示码率及单位。</summary>
    public string BitRate(int value) => value > 0 ? $"{value} kbps" : "—";
    /// <summary>展示采样率及单位。</summary>
    public string SampleRate(int value) => value > 0 ? $"{value:N0} Hz" : "—";
}
