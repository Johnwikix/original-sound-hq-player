using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

/// <summary>当前页面内的临时详情内容；不导航 Frame，编辑会话随关闭释放。</summary>
public sealed partial class MusicDetailsPanel : UserControl
{
    public MusicDetailsViewModel? ViewModel { get; private set; }
    public Music? Music => ViewModel?.Music;
    public FrameworkElement CoverElement => DetailView.CoverElement;
    public FrameworkElement TitleElement => DetailView.TitleElement;
    public void FocusHeader() => DetailView.FocusHeader();
    public void BeginClose() => ViewModel?.BeginClose();
    public MusicDetailsPanel()
    {
        InitializeComponent();
        Unloaded += Page_Unloaded;
    }
    public bool Open(MusicDetailsRequest request)
    {
        try
        {
            var app = App.Services.GetRequiredService<AppViewModel>();
            var editor = App.Services.GetRequiredService<IMusicDetailsEditorFactory>().Create(request.Music);
            ViewModel = new MusicDetailsViewModel(editor, app, App.Services.GetRequiredService<MusicDatabaseService>(),
                App.Services.GetRequiredService<EditorSessions>(), App.GetLogger<MusicDetailsPanel>(),
                MusicCommands.DetailsPlayCommand, request);
            ViewModel.CloseRequested += CloseRequested;
            ViewModel.Start();
            app.IsBackBtnEnable = true;
            Bindings.Update();
            return true;
        }
        catch (Exception exception)
        {
            App.GetLogger<MusicDetailsPanel>().LogError(exception, "打开歌曲详情失败");
            Release();
            return false;
        }
    }
    public void UpdatePlaybackContext(MusicDetailsRequest request) => ViewModel?.SetPlaybackParameter(request);

    private void CloseRequested(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, ViewModel)) App.Services.GetRequiredService<LibraryNavigationService>().CloseSongDetails();
    }
    private void Panel_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        App.Services.GetRequiredService<LibraryNavigationService>().CloseSongDetails();
        args.Handled = true;
    }
    private void Page_Unloaded(object sender, RoutedEventArgs args) => Release();
    public void Release()
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;
        ViewModel = null;
        viewModel.CloseRequested -= CloseRequested;
        viewModel.BeginClose();
        DetailView.ViewModel = null;
        Bindings.StopTracking();
        // StopAsync 自行观察异常；EditorSessions 保留该屏障直到真实工作完成。
        _ = viewModel.StopAsync();
    }
}
