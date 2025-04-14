using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.Model;
using Microsoft.UI;
using AppWindow = Microsoft.UI.Windowing.AppWindow;
using WinRT.Interop;
using Windows.Graphics;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.Services;
using ATL;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicDetailsWindow : Window
    {
        private Music musicDetail;
        public EventHandler<Music> MusicDetailChanged;
        public MusicDetailsWindow(Music music)
        {
            this.InitializeComponent();
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(MusicDetailTitleBar);
            setWindow();
            InitalizeData(music);         

        }

        private void setWindow() {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            appWindow.MoveAndResize(new RectInt32(_X: 560, _Y: 280, _Width: 800, _Height: 800));
        }

        private async void InitalizeData(Music music)
        {
            musicDetail = music;
            TitleTextBlock.Text = music.Title;
            AuthorTextBlock.Text = music.Author;
            AlbumTextBlock.Text = music.Album;
            DurationTextBlock.Text = music.Duration.ToString(@"mm\:ss");
            BitDepthTextBlock.Text = $"{music.BitDepth}bit" ;
            BitRateTextBlock.Text = $"{music.BitRate}kbps";
            SampleRateTextBlock.Text = $"{music.SampleRate}Hz";
            YearTextBlock.Text = music.Year.ToString();
            PathTextBlock.Text = music.Path;
            AlbumCoverImage.Source =await ToolUtils.LoadAlbumCover(music);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog confirmDialog = new ContentDialog
            {
                Title = "是否确认修改",               
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };
            ContentDialogResult result = await confirmDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                var music = await MusicDatabaseService.GetMusic(musicDetail.Id);
                if (music != null)
                {
                    try
                    {
                        Track theTrack = new Track(musicDetail.Path);
                        theTrack.Title = TitleTextBlock.Text;
                        theTrack.Artist = AuthorTextBlock.Text;
                        theTrack.Album = AlbumTextBlock.Text;
                        theTrack.Year = int.Parse(YearTextBlock.Text);
                        theTrack.Save();
                        music.Title = TitleTextBlock.Text;
                        music.Author = AuthorTextBlock.Text;
                        music.Album = AlbumTextBlock.Text;
                        music.Year = int.Parse(YearTextBlock.Text);
                        await MusicDatabaseService.UpdateMusicInfo(music);
                        MusicDetailChanged?.Invoke(this, music);
                        this.Close();
                    }
                    catch (Exception ex) {
                        System.Diagnostics.Debug.WriteLine(ex.Message);
                        this.Close();
                    }                    
                }                
            }            
        }
    }
}
