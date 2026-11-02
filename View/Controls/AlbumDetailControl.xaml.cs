using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>专辑级属性的展示与编辑视图；状态全部来自 AlbumDetailsViewModel，视图只负责布局与响应式排列。</summary>
public sealed partial class AlbumDetailControl : UserControl
{
    /// <summary>一次专辑详情的展示与编辑上下文，不依赖具体页面。</summary>
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(AlbumDetailsViewModel), typeof(AlbumDetailControl), new PropertyMetadata(null, ViewModelChanged));
    public AlbumDetailsViewModel? ViewModel { get => (AlbumDetailsViewModel?)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }

    /// <summary>初始化专辑属性视图，布局变化只调整视觉排列。</summary>
    public AlbumDetailControl()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
    }

    private static void ViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AlbumDetailControl)sender;
        control.DismissMenus();
        control.CoverPreviewImage.Source = null;
        control.WorkspaceTabs.SelectedIndex = 0;
        control.IdentityScroll.ChangeView(null, 0, null, true);
        control.DetailScroll.ChangeView(null, 0, null, true);
        control.Bindings?.Update();
    }

    private void Confirm_Click(object sender, RoutedEventArgs args) => ConfirmFlyout.Hide();
    private void Cancel_Click(object sender, RoutedEventArgs args) => ConfirmFlyout.Hide();
    private void OnUnloaded(object sender, RoutedEventArgs args) => DismissMenus();

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // 与 MusicDetailControl 相同断点（810/540/760）：矮窗口优先保留侧栏，极窄时才继续堆叠；专辑封面保持比歌曲大一档。
        bool shortWindow = args.NewSize.Height < 540;
        bool stacked = args.NewSize.Width < 810 && (!shortWindow || args.NewSize.Width < 760);
        bool compact = args.NewSize.Width < 810 || shortWindow;
        double gutter = compact ? 24 : 32;
        double coverSize = stacked ? 112 : compact ? 240 : 280;
        double columnGap = compact ? 24 : 36;
        DetailRoot.Padding = new Thickness(gutter, compact ? 12 : 16, gutter, compact ? 16 : 20);
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
        AlbumTitleText.FontSize = compact ? 22 : 26;
        AlbumTitleText.MaxLines = stacked ? 1 : compact ? 2 : 3;
        SummaryText.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        CoverPlaceholder.FontSize = Math.Clamp(coverSize * 0.3, 24, 96);
        Grid.SetRow(WorkspaceViewport, stacked ? 1 : 0);
        Grid.SetColumn(WorkspaceViewport, stacked ? 0 : 2);
        Grid.SetColumnSpan(WorkspaceViewport, stacked ? 3 : 1);
        FooterBar.ColumnDefinitions[0].Width = HeroLayout.ColumnDefinitions[0].Width;
        FooterBar.ColumnDefinitions[1].Width = HeroLayout.ColumnDefinitions[1].Width;
        FooterBar.ColumnDefinitions[2].Width = HeroLayout.ColumnDefinitions[2].Width;
        Grid.SetColumn(FooterButtons, stacked ? 0 : 2);
        Grid.SetColumnSpan(FooterButtons, stacked ? 3 : 1);
    }

    /// <summary>按剩余视口约束页签，避免长曲目列表撑出固定保存区。</summary>
    private void PivotViewport_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid viewport || viewport.Children.Count != 1 || viewport.Children[0] is not Pivot tabs) return;
        tabs.MaxWidth = Math.Max(0, args.NewSize.Width);
        tabs.MaxHeight = Math.Max(0, args.NewSize.Height);
    }

    /// <summary>收起专辑详情关联的独立菜单。</summary>
    public void DismissMenus()
    {
        CoverMenu.Hide();
        ConfirmFlyout.Hide();
    }

    /// <summary>加载期间保护草稿；保持启用状态，避免完成加载时输入框切换禁用样式。</summary>
    public bool TagsReadOnly(bool remote, bool loading) => remote || loading;
    /// <summary>判断是否需要显示错误反馈。</summary>
    public bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
    /// <summary>展示缺失文本的占位。</summary>
    public string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    /// <summary>无封面时显示专辑占位图标。</summary>
    public Visibility CoverPlaceholderVisibility(BitmapImage? preview) => preview is null ? Visibility.Visible : Visibility.Collapsed;
}
