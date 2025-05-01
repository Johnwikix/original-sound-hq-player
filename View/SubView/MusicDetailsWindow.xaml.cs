using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
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
        private AppWindow musicDetailAppWindow;
        private ThemeStyleHelper themeStyleHelper;
        private MainWindow mainWindow;
        public MusicDetailsWindow(Music music)
        {
            this.InitializeComponent();
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(MusicDetailTitleBar);
            setWindow();
            InitalizeData(music);
            themeStyleHelper = new ThemeStyleHelper(this, musicDetailAppWindow);
            themeStyleHelper.SetAppStyle();
            themeStyleHelper.SetAppTheme();
            mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.themeChanged += MainWindow_themeChanged;
                mainWindow.styleChanged += MainWindow_styleChanged;
            }
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
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            musicDetailAppWindow = AppWindow.GetFromWindowId(id);
            musicDetailAppWindow.SetIcon("Assets/icon.ico");
            uint dpi = GetDpiForWindow(hwnd);
            scaleFactor = dpi / 96.0;
            int originalWidth = 650;
            int originalHeight = 650;
            int adjustedWidth = (int)(originalWidth * scaleFactor);
            int adjustedHeight = (int)(originalHeight * scaleFactor);
            musicDetailAppWindow.MoveAndResize(new RectInt32(_X: 560, _Y: 280, _Width: adjustedWidth, _Height: adjustedHeight));
            notificationService = new NotificationService();
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
            albumCoverData = ToolUtils.GetRawImage(music);
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
                AppData.albumCoverCache[music.Album] = (BitmapImage)AlbumCoverImage.Source;
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            MusicDetailChanged?.Invoke(this, music);
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
                notificationService.SendNotification("¥ÌŒÛ", "ªÒ»°∏Ë¥  ß∞‹£¨«ÎºÏ≤È…Ë÷√");
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
