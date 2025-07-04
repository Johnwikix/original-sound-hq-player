using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Linq;
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
    public sealed partial class MusicDetailsWindow : Window
    {
        private Music musicDetail;
        public EventHandler<Music> MusicDetailChanged;
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        private double scaleFactor = 0;
        private NotificationService notificationService;
        private byte[] albumCoverData = null;
        private nint hwnd;
        //private AppWindow musicDetailAppWindow;
        private ThemeStyleHelper themeStyleHelper;
        private MainWindow mainWindow;
        public MusicDetailsWindow(Music music)
        {
            this.InitializeComponent();
            mainWindow = (App.MainWindow as MainWindow);
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(MusicDetailTitleBar);
            setWindow();
            InitalizeData(music);
            themeStyleHelper = new ThemeStyleHelper(this, this.AppWindow);
            themeStyleHelper.SetAppStyle();
            themeStyleHelper.SetAppTheme();
            if (mainWindow != null)
            {
                mainWindow.themeChanged += MainWindow_themeChanged;
                mainWindow.styleChanged += MainWindow_styleChanged;
                mainWindow.WindowClosed += (s, e) =>
                {
                    this.Close();
                };
            }
            Title = ToolUtils.GetString("MusicDetailTitle");
            this.Closed += MusicDetailWindow_Closed;
        }

        private void MusicDetailWindow_Closed(object sender, WindowEventArgs args)
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
            this.AppWindow.SetIcon("Assets/icon.ico");
            uint dpi = GetDpiForWindow(hwnd);
            scaleFactor = dpi / 96.0;
            int originalWidth = 650;
            int originalHeight = 750;
            int adjustedWidth = (int)(originalWidth * scaleFactor);
            int adjustedHeight = (int)(originalHeight * scaleFactor);
            WindowSizeHelper.SetMinimumSize(hwnd, this, adjustedWidth, adjustedHeight);
            // 获取主窗口句柄和信息
            IntPtr mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
            WindowId mainWindowId = Win32Interop.GetWindowIdFromWindow(mainHwnd);
            AppWindow mainAppWindow = AppWindow.GetFromWindowId(mainWindowId);
            PointInt32 mainWindowPosition = mainAppWindow.Position; // 主窗口位置（X,Y）
            int mainWindowWidth = mainAppWindow.Size.Width;         // 主窗口宽度
            int mainWindowHeight = mainAppWindow.Size.Height;       // 主窗口高度
            // 计算子窗口在主窗口中心的位置
            int centerX = mainWindowPosition.X + (mainWindowWidth - adjustedWidth) / 2;
            int centerY = mainWindowPosition.Y + (mainWindowHeight - adjustedHeight) / 2;
            this.AppWindow.MoveAndResize(new RectInt32(_X: centerX, _Y: centerY, _Width: adjustedWidth, _Height: adjustedHeight));
            notificationService = App.Services.GetRequiredService<NotificationService>();
        }
        private void MainWindow_styleChanged(object? sender, EventArgs e)
        {
            themeStyleHelper.SetAppStyle();
        }
        private void MainWindow_themeChanged(object? sender, EventArgs e)
        {
            themeStyleHelper.SetAppTheme();
        }

        private async void InitalizeData(Music music)
        {
            musicDetail = music;
            TitleTextBlock.Text = music.Title;
            AuthorTextBlock.Text = music.Author;
            AlbumTextBlock.Text = music.Album;
            TrackNumberBox.Text = music.TrackNumber.ToString();
            LyricsTextBox.Text = music.Lyrics;
            DurationTextBlock.Text = music.Duration.ToString(@"mm\:ss");
            BitDepthTextBlock.Text = $"{music.BitDepth}bit";
            BitRateTextBlock.Text = $"{music.BitRate}kbps";
            SampleRateTextBlock.Text = $"{music.SampleRate}Hz";
            YearTextBlock.Text = music.Year.ToString();
            LastFolderNameTextBlock.Text = music.LastLevelFolderPath;
            PathTextBlock.Text = music.Path;
            albumCoverData = await ToolUtils.GetRawImage(music);
            AlbumCoverImage.Source = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async Task UpdateFile(Music music)
        {
            using (TagLib.File audioFile = TagLib.File.Create(music.Path))
            {
                Tag tag = audioFile.Tag;
                tag.Pictures = Array.Empty<IPicture>();
                byte[] albumArtData = albumCoverData;
                Picture albumArt = new Picture
                {
                    Type = PictureType.FrontCover,
                    MimeType = System.Net.Mime.MediaTypeNames.Image.Jpeg,
                    Description = "Album Art",
                    Data = new ByteVector(albumArtData)
                };
                tag.Pictures = new IPicture[] { albumArt };
                tag.Title = TitleTextBlock.Text;
                tag.Album = AlbumTextBlock.Text;
                tag.Performers = new string[] { AuthorTextBlock.Text };
                tag.Track = uint.Parse(TrackNumberBox.Text);
                tag.Year = uint.Parse(YearTextBlock.Text);
                tag.Lyrics = LyricsTextBox.Text;
                LoadingGrid.Visibility = Visibility.Visible;
                MusicDetail.Visibility = Visibility.Collapsed;
                await Task.Run(() => audioFile.Save());
            }
            music.Title = TitleTextBlock.Text;
            music.Author = AuthorTextBlock.Text;
            music.Album = AlbumTextBlock.Text;
            music.Year = int.Parse(YearTextBlock.Text);
            music.TrackNumber = int.Parse(TrackNumberBox.Text);
            music.Lyrics = LyricsTextBox.Text;
            await MusicDatabaseService.UpdateMusicInfo(music);
            if (AppData.albumCoverCache.ContainsKey(music.Album))
            {
                //AppData.albumCoverCache[music.Album] = (BitmapImage)AlbumCoverImage.Source;
                AppData.albumCoverCache.SetValue(music.Album, (BitmapImage)AlbumCoverImage.Source);
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            MusicDetailChanged?.Invoke(this, music);
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
            var music = AppData.allSongs.Where(m => m.Id == musicDetail.Id).FirstOrDefault();
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
                notificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("FailedObtainCover"));
            }
        }

        private async void GetLyricsFromNet_Click(object sender, RoutedEventArgs e)
        {
            LrcService lrcService = new LrcService();
            string lyrics = await lrcService.GetLyricsAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            if (lyrics != null)
            {
                LyricsTextBox.Text = lyrics;
            }
            else
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("FailedObtainLyrics"));
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

        private async void SaveLyricsToDateBase_Click(object sender, RoutedEventArgs e)
        {
            var music = AppData.allSongs.Where(m => m.Id == musicDetail.Id).FirstOrDefault();
            if (music != null)
            {
                music.Lyrics = LyricsTextBox.Text;
                await MusicDatabaseService.UpdateMusicInfo(music);
            }
            this.Close();
        }
    }
}
