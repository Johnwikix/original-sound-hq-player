using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TagLib;
using Windows.Storage;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.OnlineAPIs.CloudMusicAPI;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.WebService;
using ZLinq;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AlbumDetailWindow : WinUIEx.WindowEx
    {
        private Music musicDetail;
        public EventHandler<Music> AlbumDetailChanged;
        private NotificationService notificationService;
        private byte[] albumCoverData = null;
        private nint hwnd;
        private ThemeStyleHelper themeStyleHelper;
        public AlbumDetailWindow(Music music)
        {
            this.InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AlbumDetailTitleBar);
            setWindow();
            InitalizeData(music);
            themeStyleHelper = new ThemeStyleHelper(this, this.AppWindow);
            themeStyleHelper.SetAppStyle();
            themeStyleHelper.SetAppTheme();
            if (App.MainWindow is not null)
            {
                App.MainWindow.themeChanged += MainWindow_themeChanged;
                App.MainWindow.styleChanged += MainWindow_styleChanged;
                App.MainWindow.customStyleChanged += MainWindow_customStyleChanged;
                App.MainWindow.backdropInputState += MainWindow_backdropInputState;
            }
            Title = ToolUtils.GetString("AlbumDetailTitle");
            this.Closed += AlbumDetailWindow_Closed;
        }

        private void MainWindow_backdropInputState(object? sender, bool e)
        {
            themeStyleHelper?.UpdateBackdropActiveState(e);
        }

        private void AlbumDetailWindow_Closed(object sender, WindowEventArgs args)
        {
            if (App.MainWindow is not null)
            {
                App.MainWindow.themeChanged -= MainWindow_themeChanged;
                App.MainWindow.styleChanged -= MainWindow_styleChanged;
                App.MainWindow.customStyleChanged -= MainWindow_customStyleChanged;
                App.MainWindow.backdropInputState -= MainWindow_backdropInputState;
            }
            this.Closed -= AlbumDetailWindow_Closed;
        }

        private void setWindow()
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowSizeHelper.ResizeWindowAndCenterInMainWindow(hwnd, 700, 550, App.MainWindow.AppWindow, this.AppWindow);
            this.AppWindow.SetIcon("Assets/icon.ico");
            notificationService = App.Services.GetRequiredService<NotificationService>();
        }
        private void MainWindow_customStyleChanged(object? sender, EventArgs e)
        {
            themeStyleHelper.ChangeCustomAcrylicStyle();
        }

        private void MainWindow_styleChanged(object? sender, EventArgs e)
        {
            themeStyleHelper.SetAppStyle();
        }

        private void MainWindow_themeChanged(object? sender, EventArgs e)
        {
            themeStyleHelper.SetAppTheme();
        }

        private async void InitalizeData(Music album)
        {
            musicDetail = album;
            AlbumTextBlock.Text = album.Album;
            YearTextBlock.Text = album.Year.ToString();
            albumCoverData = await ToolUtils.GetRawImage(album, true);
            AlbumCoverImage.Source = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async Task UpdateFile(Music album)
        {
            LoadingGrid.Visibility = Visibility.Visible;
            AlbumDetail.Visibility = Visibility.Collapsed;
            IEnumerable<Music> musics = MusicDatabaseService.FindMusicListByAlbum(album.Album);
            Music result = null;
            // 避免重复写入标志位
            bool isResultAssigned = false;
            foreach (var music in musics)
            {
                using (TagLib.File audioFile = TagLib.File.Create(music.Path))
                {
                    Tag tag = audioFile.Tag;
                    tag.Pictures = Array.Empty<IPicture>();
                    if (albumCoverData is not null)
                    {
                        byte[] albumArtData = albumCoverData;
                        Picture albumArt = new Picture
                        {
                            Type = PictureType.FrontCover,
                            MimeType = System.Net.Mime.MediaTypeNames.Image.Jpeg,
                            Description = "Album Art",
                            Data = new ByteVector(albumArtData)
                        };
                        tag.Pictures = new IPicture[] { albumArt };
                    }
                    tag.Album = AlbumTextBlock.Text;
                    tag.Year = uint.Parse(YearTextBlock.Text);
                    await Task.Run(() => audioFile.Save());
                }
                music.Album = AlbumTextBlock.Text;
                music.Year = int.Parse(YearTextBlock.Text);

                await MusicDatabaseService.UpdateMusicInfo(music);
                if (!isResultAssigned)
                {
                    if (AppData.albumCoverCache.ContainsKey(music.Album))
                    {
                        AppData.albumCoverCache[music.Album] = (BitmapImage)AlbumCoverImage.Source;
                    }
                    result = music;
                    isResultAssigned = true;
                }
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            AlbumDetailChanged?.Invoke(this, result);
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
            var music = AppData.allSongs.AsValueEnumerable().Where(m => m.Id == musicDetail.Id).FirstOrDefault();
            if (music is not null)
            {
                try
                {
                    await UpdateFile(music);
                }
                catch (Exception ex)
                {
                    notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                }
            }
            this.Close();
        }

        private async void GetImageFromNet_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(AppSettings.LrcAPISource) || AppSettings.LrcAPISource == "https://api.lrc.cx")
            {
                albumCoverData = await CloudMusicSearchHelper.GetSongAlbum(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            }
            else
            {
                albumCoverData = await LrcService.GetCoverImageAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            }
            if (albumCoverData is not null)
            {
                AlbumCoverImage.Source = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
            }
            else
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("FailedObtainCover"));
            }
        }

        private async void SelectCoverImageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openPicker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow.AppWindow.Id);
                // 添加m3u8文件筛选器
                openPicker.FileTypeFilter.Add(".jpg");
                openPicker.FileTypeFilter.Add(".jpeg");
                openPicker.FileTypeFilter.Add(".png");
                openPicker.ViewMode = PickerViewMode.List;
                // 显示文件选择器并获取结果
                var filePickerResult = await openPicker.PickSingleFileAsync();
                var file = await StorageFile.GetFileFromPathAsync(filePickerResult.Path);

                if (file is not null)
                {
                    albumCoverData = await System.IO.File.ReadAllBytesAsync(file.Path);
                    AlbumCoverImage.Source = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
                }
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }

        private async void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker(App.MainWindow.AppWindow.Id);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("JPEG Image", new[] { ".jpg" });
            picker.SuggestedFileName = "SavedImage";
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                try
                {
                    await System.IO.File.WriteAllBytesAsync(file.Path, albumCoverData);
                }
                catch (Exception ex)
                {
                    notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                }
            }
        }

        private void CloseFlyoutButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
        }
    }
}
