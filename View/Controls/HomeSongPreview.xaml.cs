using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>复用首页曲目和命令，以独立覆盖层从原封面展开详情。</summary>
public sealed partial class HomeSongPreview : UserControl
{
    /// <summary>标识当前详情曲目；关闭时清空以释放封面消费者。</summary>
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(HomeSong), typeof(HomeSongPreview), new PropertyMetadata(null, SongChanged));
    /// <summary>标识歌单菜单复用的首页 ViewModel。</summary>
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(HomeViewModel), typeof(HomeSongPreview), new PropertyMetadata(null));
    private readonly UISettings _settings = new();
    private Vector3KeyFrameAnimation? _scale;
    private Vector3KeyFrameAnimation? _translation;
    private ScalarKeyFrameAnimation? _reveal;
    private InsetClip? _clip;
    private Visual? _visual;
    private CompositionScopedBatch? _closingBatch;
    private Vector3 _originTranslation;
    private float _originScale = 1;
    /// <summary>获取或设置当前详情曲目。</summary>
    public HomeSong? Song { get => (HomeSong?)GetValue(SongProperty); set => SetValue(SongProperty, value); }
    /// <summary>获取或设置歌单菜单的状态与命令来源。</summary>
    public HomeViewModel? ViewModel { get => (HomeViewModel?)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    /// <summary>获取歌单菜单是否打开，菜单打开期间宿主保留详情。</summary>
    public bool IsPlaylistMenuOpen { get; private set; }
    /// <summary>在菜单进入或离开独立弹层时通知宿主更新悬停保留状态。</summary>
    public event EventHandler? MenuStateChanged;
    /// <summary>在退出动画实际完成后通知宿主释放曲目，取消退出时不发送。</summary>
    public event EventHandler? CloseAnimationCompleted;

    /// <summary>请求进入属性面板，供触摸和辅助技术复用同一交互入口。</summary>
    public event EventHandler? Invoked;

    internal void Invoke()
    {
        if (IsLoaded && IsEnabled && Visibility == Visibility.Visible) Invoked?.Invoke(this, EventArgs.Empty);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PreviewAutomationPeer(this);

    private sealed partial class PreviewAutomationPeer(HomeSongPreview owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(HomeSongPreview);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetNameCore() => owner.Song?.Music.Title ?? base.GetNameCore();
        protected override object GetPatternCore(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);
        public void Invoke() => owner.DispatcherQueue.TryEnqueue(owner.Invoke);
    }

    /// <summary>初始化详情模板，合成对象只在面板第一次显示后创建。</summary>
    public HomeSongPreview()
    {
        InitializeComponent();
        PanelRoot.Translation = new Vector3(0, 0, 48);
        Unloaded += OnUnloaded;
    }
    private static void SongChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var preview = (HomeSongPreview)sender;
        preview.Artwork.Music = preview.Song?.Music;
    }

    /// <summary>在原封面基础上加宽详情；高度不足时收敛封面区域，不反向缩窄整张卡片。</summary>
    public void Fit(Size available, Size sourceArtwork)
    {
        double aspectRatio = Math.Clamp(sourceArtwork.Width / sourceArtwork.Height, .25, 4);
        Width = Math.Min(Math.Max(360, sourceArtwork.Width + 48), available.Width - 24);
        Details.Measure(new Size(Width - 2, double.PositiveInfinity));
        double coverBudget = Math.Max(0, available.Height - 26 - Details.DesiredSize.Height);
        CoverHost.Height = Math.Min((Width - 2) / aspectRatio, coverBudget);
    }
    private void PlaylistMenu_Opening(object sender, object args)
    {
        IsPlaylistMenuOpen = true;
        MenuStateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void PlaylistMenu_Closed(object sender, object args)
    {
        IsPlaylistMenuOpen = false;
        MenuStateChanged?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>收起歌单弹层，避免关闭详情或离页后留下独立菜单。</summary>
    public void DismissMenus()
    {
        PlaylistMenu.Hide();
        IsPlaylistMenuOpen = false;
    }

    /// <summary>从原卡片的封面位置展开；平移、缩放和裁切均由合成线程执行。</summary>
    public void Reveal(Rect source, Point destination)
    {
        CancelClosing();
        EnsureAnimation();
        if (_visual is null || _clip is null) return;
        _originScale = (float)(source.Width / ActualWidth);
        _originTranslation = new Vector3((float)(source.X - destination.X), (float)(source.Y - destination.Y), 0);
        _visual.StopAnimation("Scale");
        _visual.StopAnimation("Translation");
        _visual.StopAnimation("Opacity");
        _clip.StopAnimation("BottomInset");
        _visual.Scale = Vector3.One;
        _visual.Properties.InsertVector3("Translation", Vector3.Zero);
        _visual.Opacity = 1;
        _clip.BottomInset = 0;
        if (!_settings.AnimationsEnabled) return;
        _scale!.Duration = TimeSpan.FromMilliseconds(240);
        _translation!.Duration = _scale.Duration;
        _reveal!.Duration = _scale.Duration;
        _scale.InsertKeyFrame(0, new Vector3(_originScale, _originScale, 1));
        _scale.InsertKeyFrame(1, Vector3.One);
        _translation.InsertKeyFrame(0, _originTranslation);
        _translation.InsertKeyFrame(1, Vector3.Zero);
        _reveal.InsertKeyFrame(0, (float)Math.Max(0, ActualHeight - CoverHost.ActualHeight));
        _reveal.InsertKeyFrame(1, 0);
        _visual.StartAnimation("Scale", _scale);
        _visual.StartAnimation("Translation", _translation);
        _clip.StartAnimation("BottomInset", _reveal);
    }

    /// <summary>缩回原封面；调用方在动画结束后清除曲目和覆盖层。</summary>
    public void CollapseToCover()
    {
        CancelClosing();
        if (_visual is null || _clip is null || !_settings.AnimationsEnabled)
        {
            CloseAnimationCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }
        _closingBatch = _visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _closingBatch.Completed += ClosingCompleted;
        _scale!.Duration = TimeSpan.FromMilliseconds(200);
        _translation!.Duration = _scale.Duration;
        _reveal!.Duration = _scale.Duration;
        _scale.InsertExpressionKeyFrame(0, "this.StartingValue");
        _scale.InsertKeyFrame(1, new Vector3(_originScale, _originScale, 1));
        _translation.InsertExpressionKeyFrame(0, "this.StartingValue");
        _translation.InsertKeyFrame(1, _originTranslation);
        _reveal.InsertExpressionKeyFrame(0, "this.StartingValue");
        _reveal.InsertKeyFrame(1, (float)Math.Max(0, ActualHeight - CoverHost.ActualHeight));
        // 封面保持不透明地缩回；整层淡出会让实心背景与下方缩略图重复混合，产生末段明暗脉冲。
        // 正文仍由原有 BottomInset 裁切收起，完成后再隐藏整个面板。
        _visual.StartAnimation("Scale", _scale);
        _visual.StartAnimation("Translation", _translation);
        _clip.StartAnimation("BottomInset", _reveal);
        _closingBatch.End();
    }
    private void ClosingCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, _closingBatch)) return;
        CancelClosing();
        CloseAnimationCompleted?.Invoke(this, EventArgs.Empty);
    }
    private void CancelClosing()
    {
        if (_closingBatch is null) return;
        _closingBatch.Completed -= ClosingCompleted;
        _closingBatch.Dispose();
        _closingBatch = null;
    }

    /// <summary>把键盘操作入口移入详情卡。</summary>
    public void FocusActions() => PlayButton.Focus(FocusState.Keyboard);

    /// <summary>撤销正在收起的动画，防止重新进入面板时继续淡出。</summary>
    public void Restore()
    {
        CancelClosing();
        if (_visual is null || _clip is null) return;
        _visual.StopAnimation("Scale");
        _visual.StopAnimation("Translation");
        _visual.StopAnimation("Opacity");
        _clip.StopAnimation("BottomInset");
        _visual.Scale = Vector3.One;
        _visual.Properties.InsertVector3("Translation", Vector3.Zero);
        _visual.Opacity = 1;
        _clip.BottomInset = 0;
    }

    /// <summary>隐藏后保持透明，不把退出终态复位成全尺寸可见的一帧；下次 Reveal 再初始化。</summary>
    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        CancelClosing();
        if (_visual is null) return;
        _visual.StopAnimation("Scale");
        _visual.StopAnimation("Translation");
        _visual.StopAnimation("Opacity");
        _clip?.StopAnimation("BottomInset");
        _visual.Opacity = 0;
    }

    private void EnsureAnimation()
    {
        if (_visual is not null) return;
        _visual = ElementCompositionPreview.GetElementVisual(this);
        ElementCompositionPreview.SetIsTranslationEnabled(this, true);
        var compositor = _visual.Compositor;
        _scale = compositor.CreateVector3KeyFrameAnimation();
        _translation = compositor.CreateVector3KeyFrameAnimation();
        _reveal = compositor.CreateScalarKeyFrameAnimation();
        _clip = compositor.CreateInsetClip();
        _visual.Clip = _clip;
        // .NET 10+：复用动画对象，悬停移动不创建逐帧任务或闭包。
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        DismissMenus();
        CancelClosing();
        if (_visual is not null)
        {
            _visual.StopAnimation("Scale");
            _visual.StopAnimation("Translation");
            _visual.StopAnimation("Opacity");
            _visual.Clip = null;
        }
        _scale?.Dispose();
        _translation?.Dispose();
        _reveal?.Dispose();
        _clip?.Dispose();
        _scale = null;
        _translation = null;
        _reveal = null;
        _clip = null;
        _visual = null;
    }
}
