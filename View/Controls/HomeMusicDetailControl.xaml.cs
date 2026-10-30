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
        control.WorkspaceTabs.SelectedIndex = 0;
        control.LyricsTabs.SelectedIndex = 0;
        control.IdentityScroll.ChangeView(null, 0, null, true);
        control.DetailScroll.ChangeView(null, 0, null, true);
        control.FileScroll.ChangeView(null, 0, null, true);
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
        // 矮窗口优先保留侧栏，避免封面占去编辑区的垂直空间；极窄时才继续堆叠。
        bool shortWindow = args.NewSize.Height < 640;
        bool stacked = args.NewSize.Width < 960 && (!shortWindow || args.NewSize.Width < 760);
        bool compact = args.NewSize.Width < 960 || shortWindow;
        double gutter = compact ? 24 : 32;
        double coverSize = stacked ? 112 : compact ? 200 : 240;
        double columnGap = compact ? 24 : 36;
        DetailRoot.Padding = new Thickness(gutter, compact ? 12 : 16, gutter, compact ? 16 : 20);
        HeaderBar.Margin = new Thickness(0, 0, 0, compact ? 12 : 20);
        HeroLayout.RowSpacing = stacked ? 12 : 0;
        HeroLayout.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(coverSize);
        HeroLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : columnGap);
        HeroLayout.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HeroLayout.RowDefinitions[0].Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        HeroLayout.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumnSpan(IdentityScroll, stacked ? 3 : 1);
        IdentityScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        ArtworkRegion.Width = ArtworkRegion.Height = coverSize;
        CoverHost.Width = CoverHost.Height = coverSize;
        IdentityLayout.ColumnDefinitions[0].Width = new GridLength(coverSize);
        Grid.SetColumnSpan(ArtworkRegion, stacked ? 1 : 2);
        Grid.SetRow(HeroText, stacked ? 0 : 1);
        Grid.SetColumn(HeroText, stacked ? 1 : 0);
        Grid.SetColumnSpan(HeroText, stacked ? 1 : 2);
        HeroText.Margin = stacked ? new Thickness(20, 0, 0, 0) : new Thickness(0, 20, 0, 0);
        SongTitleText.FontSize = compact ? 22 : 26;
        SongTitleText.MaxLines = stacked ? 1 : compact ? 2 : 3;
        SongAlbumText.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        RecommendationText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(WorkspaceViewport, stacked ? 1 : 0);
        Grid.SetColumn(WorkspaceViewport, stacked ? 0 : 2);
        Grid.SetColumnSpan(WorkspaceViewport, stacked ? 3 : 1);
        FooterBar.ColumnDefinitions[0].Width = HeroLayout.ColumnDefinitions[0].Width;
        FooterBar.ColumnDefinitions[1].Width = HeroLayout.ColumnDefinitions[1].Width;
        FooterBar.ColumnDefinitions[2].Width = HeroLayout.ColumnDefinitions[2].Width;
        Grid.SetColumn(FooterButtons, stacked ? 0 : 2);
        Grid.SetColumnSpan(FooterButtons, stacked ? 3 : 1);
    }
    /// <summary>按剩余视口约束页签，避免长表单或歌词撑出固定保存区。</summary>
    private void PivotViewport_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid viewport || viewport.Children.Count != 1 || viewport.Children[0] is not Pivot tabs) return;
        tabs.MaxWidth = Math.Max(0, args.NewSize.Width);
        tabs.MaxHeight = Math.Max(0, args.NewSize.Height);
    }
    /// <summary>收起详情关联的独立菜单。</summary>
    public void DismissMenus()
    {
        PlaylistMenu.Hide();
        CoverMenu.Hide();
        LyricsToolsMenu.Hide();
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
