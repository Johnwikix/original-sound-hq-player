using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TagLib;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.WebService;
using AppWindow = Microsoft.UI.Windowing.AppWindow;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AlbumDetailWindow : Window
    {
        private Music musicDetail;
        public EventHandler<Music> AlbumDetailChanged;
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        private double scaleFactor = 0;
        private NotificationService notificationService;
        private byte[] albumCoverData = null;
        private nint hwnd;
        private AppWindow albumDetailAppWindow;
        private MainWindow mainWindow;
        private BitmapImage albumCoverBitmapImage;
        private ThemeStyleHelper themeStyleHelper;
        public AlbumDetailWindow(Music music)
        {
            this.InitializeComponent();
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AlbumDetailTitleBar);
            setWindow();
            InitalizeData(music);
            themeStyleHelper = new ThemeStyleHelper(this, albumDetailAppWindow);
            themeStyleHelper.SetAppStyle();
            themeStyleHelper.SetAppTheme();
            mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.themeChanged += MainWindow_themeChanged;
                mainWindow.styleChanged += MainWindow_styleChanged;
            }
            Title = ToolUtils.GetString("AlbumDetailTitle");
            this.Closed += AlbumDetailWindow_Closed;
        }

        private void AlbumDetailWindow_Closed(object sender, WindowEventArgs args)
        {
            if (mainWindow != null)
            {
                mainWindow.themeChanged -= MainWindow_themeChanged;
                mainWindow.styleChanged -= MainWindow_styleChanged;
            }
        }

        private void setWindow()
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            albumDetailAppWindow = AppWindow.GetFromWindowId(id);
            albumDetailAppWindow.SetIcon("Assets/icon.ico");
            uint dpi = GetDpiForWindow(hwnd);
            scaleFactor = dpi / 96.0;
            int originalWidth = 550;
            int originalHeight = 700;
            int adjustedWidth = (int)(originalWidth * scaleFactor);
            int adjustedHeight = (int)(originalHeight * scaleFactor);
            albumDetailAppWindow.MoveAndResize(new RectInt32(_X: 560, _Y: 280, _Width: adjustedWidth, _Height: adjustedHeight));
            notificationService = new NotificationService();
        }

        private void MainWindow_styleChanged(object? sender, EventArgs e)
        {
            //SetAppStyle();
            themeStyleHelper.SetAppStyle();
        }

        private void MainWindow_themeChanged(object? sender, EventArgs e)
        {
            //SetAppTheme();
            themeStyleHelper.SetAppTheme();
        }

        private async void InitalizeData(Music album)
        {
            musicDetail = album;
            AlbumTextBlock.Text = album.Album;
            YearTextBlock.Text = album.Year.ToString();
            albumCoverData = ToolUtils.GetRawImage(album);
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
            List<Music> musics = MusicDatabaseService.FindMusicListByAlbum(album.Album);
            Music result = null;
            // 避免重复写入标志位
            bool isResultAssigned = false;
            foreach (var music in musics)
            {
                using (TagLib.File audioFile = TagLib.File.Create(music.Path))
                {
                    Tag tag = audioFile.Tag;
                    tag.Pictures = Array.Empty<IPicture>();
                    if (albumCoverData != null)
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
                    result.Cover = (BitmapImage)AlbumCoverImage.Source;
                    isResultAssigned = true;
                }
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            AlbumDetailChanged?.Invoke(this, result);
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
            var music = await MusicDatabaseService.GetMusic(musicDetail.Id);
            if (music != null)
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
            LrcService lrcService = new LrcService();
            albumCoverData = await lrcService.GetCoverImageAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            if (albumCoverData != null)
            {
                AlbumCoverImage.Source = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
            }
            else
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), "获取封面失败，请检查设置");
            }
        }

        private async void SelectCoverImageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker openPicker = new FileOpenPicker();
                openPicker.ViewMode = PickerViewMode.Thumbnail;
                openPicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                openPicker.FileTypeFilter.Add(".jpg");
                openPicker.FileTypeFilter.Add(".jpeg");
                openPicker.FileTypeFilter.Add(".png");
                WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);
                StorageFile file = await openPicker.PickSingleFileAsync();
                if (file != null)
                {
                    using (Stream stream = await file.OpenStreamForReadAsync())
                    {
                        albumCoverData = new byte[stream.Length];
                        await stream.ReadAsync(albumCoverData, 0, (int)stream.Length);
                        AlbumCoverImage.Source = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
                    }
                }
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }

        private async void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("JPEG Image", new[] { ".jpg" });
            picker.SuggestedFileName = "SavedImage";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            StorageFile file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                try
                {
                    using (Stream stream = await file.OpenStreamForWriteAsync())
                    {
                        await stream.WriteAsync(albumCoverData, 0, albumCoverData.Length);
                    }
                    Console.WriteLine("图片已成功保存。");
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
