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
        public AlbumDetailWindow current;
        private AppWindow albumDetailAppWindow;
        public AlbumDetailWindow(Music music)
        {
            this.InitializeComponent();
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AlbumDetailTitleBar);
            setWindow();
            InitalizeData(music);
            current = this;
            SetAppStyle();
            SetAppTheme();
        }

        public void SetAppStyle()
        {
            switch (AppSettings.AppStyle)
            {
                case "Acrylic":
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                    break;
                case "Mica":
                    SystemBackdrop = new MicaBackdrop();
                    break;
                default:
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                    break;
            }
        }

        public void SetAppTheme()
        {
            Microsoft.UI.Windowing.AppWindowTitleBar m_TitleBar = albumDetailAppWindow.TitleBar;
            if (current.Content is FrameworkElement rootElement)
            {
                switch (AppSettings.AppTheme)
                {
                    case "Default":
                        m_TitleBar.ButtonForegroundColor = null;
                        m_TitleBar.ButtonHoverForegroundColor = null;
                        m_TitleBar.ButtonPressedForegroundColor = null;
                        m_TitleBar.ButtonHoverBackgroundColor = null;
                        m_TitleBar.ButtonPressedBackgroundColor = null;
                        rootElement.RequestedTheme = ElementTheme.Default;
                        break;
                    case "Dark":
                        rootElement.RequestedTheme = ElementTheme.Dark;
                        m_TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                        m_TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                        m_TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                        m_TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
                        m_TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 80, 80, 80);
                        break;
                    case "Light":
                        rootElement.RequestedTheme = ElementTheme.Light;
                        m_TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                        m_TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                        m_TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
                        m_TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
                        m_TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 190, 190, 190);
                        break;
                    default:
                        rootElement.RequestedTheme = ElementTheme.Default;
                        break;
                }
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
            var mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null) {
                mainWindow.themeChanged += MainWindow_themeChanged;
                mainWindow.styleChanged += MainWindow_styleChanged;
            }
        }

        private void MainWindow_styleChanged(object? sender, EventArgs e)
        {
            SetAppStyle();
        }

        private void MainWindow_themeChanged(object? sender, EventArgs e)
        {
            SetAppTheme();
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
            List<Music> musics = await MusicDatabaseService.FindMusicListByAlbum(album.Album);
            Music result = null;
            bool isResultAssigned = false;
            foreach (var music in musics)
            {
                using (TagLib.File audioFile = TagLib.File.Create(music.Path))
                {
                    Tag tag = audioFile.Tag;
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
                    LoadingGrid.Visibility = Visibility.Visible;
                    audioFile.Save();
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
                }
            }
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
                    notificationService.SendNotification("¥ÌŒÛ", ex.Message);
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
                notificationService.SendNotification("¥ÌŒÛ", "ªÒ»°∑‚√Ê ß∞‹£¨«ÎºÏ≤È…Ë÷√");
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
                notificationService.SendNotification("¥ÌŒÛ", ex.Message);
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
                    Console.WriteLine("Õº∆¨“—≥…π¶±£¥Ê°£");
                }
                catch (Exception ex)
                {
                    notificationService.SendNotification("¥ÌŒÛ", ex.Message);
                }
            }
        }

        private void CloseFlyoutButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
        }
    }
}
