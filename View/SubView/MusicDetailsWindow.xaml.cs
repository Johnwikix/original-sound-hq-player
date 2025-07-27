using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
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
using WinUIMusicPlayer.OnlineAPIs.CloudMusicAPI;
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
        //private double scaleFactor = 0;
        private NotificationService notificationService;
        private byte[] albumCoverData = null;
        private nint hwnd;
        //private AppWindow musicDetailAppWindow;
        private ThemeStyleHelper themeStyleHelper;
        //private MainWindow mainWindow;
        public MusicDetailsWindow(Music music)
        {
            this.InitializeComponent();
            //mainWindow = (App.MainWindow as MainWindow);
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(MusicDetailTitleBar);
            setWindow();
            InitalizeData(music);
            themeStyleHelper = new ThemeStyleHelper(this, this.AppWindow);
            themeStyleHelper.SetAppStyle();
            themeStyleHelper.SetAppTheme();
            if (App.MainWindow != null)
            {
                App.MainWindow.themeChanged += MainWindow_themeChanged;
                App.MainWindow.styleChanged += MainWindow_styleChanged;
                App.MainWindow.WindowClosed += (s, e) =>
                {
                    this.Close();
                };
            }
            Title = ToolUtils.GetString("MusicDetailTitle");
            this.Closed += MusicDetailWindow_Closed;
        }

        private void MusicDetailWindow_Closed(object sender, WindowEventArgs args)
        {
            if (App.MainWindow != null)
            {
                App.MainWindow.themeChanged -= MainWindow_themeChanged;
                App.MainWindow.styleChanged -= MainWindow_styleChanged;
            }
        }

        private void setWindow()
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowSizeHelper.SetMinimumSize(hwnd,this,650,750);
            WindowSizeHelper.ResizeWindowAndCenterInMainWindow(hwnd, 750,650,App.MainWindow.AppWindow,this.AppWindow);
            this.AppWindow.SetIcon("Assets/icon.ico");
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
            TrackNumberBox.Value = music.TrackNumber;
            LyricsTextBox.Text = music.Lyrics;
            DurationTextBlock.Text = music.Duration.ToString(@"mm\:ss");
            BitDepthTextBlock.Text = $"{music.BitDepth}bit";
            BitRateTextBlock.Text = $"{music.BitRate}kbps";
            SampleRateTextBlock.Text = $"{music.SampleRate}Hz";
            YearTextBlock.Value = music.Year;
            LastFolderNameTextBlock.Text = music.LastLevelFolderPath;
            DiskNumberBox.Value = music.DiskNumber;
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
                tag.Track = (uint)TrackNumberBox.Value;
                tag.Disc = (uint)DiskNumberBox.Value;
                tag.Year = (uint)YearTextBlock.Value;
                tag.Lyrics = LyricsTextBox.Text;
                LoadingGrid.Visibility = Visibility.Visible;
                MusicDetail.Visibility = Visibility.Collapsed;
                await Task.Run(() => audioFile.Save());
            }
            music.Title = TitleTextBlock.Text;
            music.Author = AuthorTextBlock.Text;
            music.Album = AlbumTextBlock.Text;
            music.Year = (int)YearTextBlock.Value;
            music.DiskNumber = (int)DiskNumberBox.Value;
            music.TrackNumber = (int)TrackNumberBox.Value;
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
            if (string.IsNullOrEmpty(AppSettings.LrcAPISource) || AppSettings.LrcAPISource == "https://api.lrc.cx")
            {
                albumCoverData = await CloudMusicSearchHelper.GetSongAlbum(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            }
            else
            {
                albumCoverData = await LrcService.GetCoverImageAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            }            
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
            string lyrics = await ToolUtils.GetLyricsFromNet(musicDetail);
            //if (string.IsNullOrEmpty(AppSettings.LrcAPISource) || AppSettings.LrcAPISource == "https://api.lrc.cx")
            //{
            //    lyrics = await CloudMusicSearchHelper.GetSongLyrics(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            //}
            //else
            //{
            //    lyrics = await LrcService.GetLyricsAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            //}            
            if (lyrics != null)
            {
                LyricsTextBox.Text = lyrics;
            }
            else
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("FailedObtainLyrics"));
            }
        }

        private async void SaveLyrics_Click(object sender, RoutedEventArgs e)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitizedFileName = ToolUtils.SanitizeFileName(Path.GetFileName(musicDetail.Path), invalidChars);
            string targetBasePath = Path.GetDirectoryName(musicDetail.Path);
            _=Task.Run(() =>
            {
                string lrcFileName = Path.ChangeExtension(sanitizedFileName, ".lrc");
                string lrcFilePath = Path.Combine(targetBasePath, lrcFileName);
                System.IO.File.WriteAllText(lrcFilePath, ToolUtils.ConvertLyrics(musicDetail.Lyrics));
                ToolUtils.OpenFileInExplorer(lrcFilePath);
            });
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            ToolUtils.OpenFileInExplorer(musicDetail.Path);
        }

        private  void ReadLyricsFromFile_Click(object sender, RoutedEventArgs e)
        {
            _=Task.Run(() => {
                var lyrics =ToolUtils.GetLyricsFromFile(musicDetail.Path);
                if (!string.IsNullOrEmpty(lyrics)) {
                    DispatcherQueue.TryEnqueue(() => {
                        LyricsTextBox.Text = lyrics;
                    });
                }
            });            
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
