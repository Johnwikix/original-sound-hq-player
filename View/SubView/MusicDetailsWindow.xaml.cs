using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.IO;
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
    public sealed partial class MusicDetailsWindow : WinUIEx.WindowEx
    {
        private Music musicDetail;
        public EventHandler<Music> MusicDetailChanged;
        private NotificationService notificationService;
        private byte[] albumCoverData = null;
        private nint hwnd;
        private ThemeStyleHelper themeStyleHelper;
        public MusicDetailsWindow(Music music)
        {
            this.InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(MusicDetailTitleBar);
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
            Title = ToolUtils.GetString("MusicDetailTitle");
            this.Closed += MusicDetailWindow_Closed;
        }

        private void MainWindow_backdropInputState(object? sender, bool e)
        {
            themeStyleHelper?.UpdateBackdropActiveState(e);
        }

        private void MusicDetailWindow_Closed(object sender, WindowEventArgs args)
        {
            if (App.MainWindow is not null)
            {
                App.MainWindow.themeChanged -= MainWindow_themeChanged;
                App.MainWindow.styleChanged -= MainWindow_styleChanged;
                App.MainWindow.customStyleChanged -= MainWindow_customStyleChanged;
                App.MainWindow.backdropInputState -= MainWindow_backdropInputState;
            }
            this.Closed -= MusicDetailWindow_Closed;
        }

        private void setWindow()
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowSizeHelper.ResizeWindowAndCenterInMainWindow(hwnd, 850, 650, App.MainWindow.AppWindow, this.AppWindow);
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

        private async void InitalizeData(Music music)
        {
            musicDetail = music;
            TitleTextBlock.Text = music.Title;
            AuthorTextBlock.Text = music.Author;
            AlbumTextBlock.Text = music.Album;
            TrackNumberBox.Value = music.TrackNumber;
            LyricsTextBox.Text = music.Lyrics;
            DurationTextBlock.Text = music.Duration.TotalHours >= 1 ? music.Duration.ToString(@"hh\:mm\:ss") : music.Duration.ToString(@"mm\:ss");
            BitDepthTextBlock.Text = $"{music.BitDepth}bit";
            BitRateTextBlock.Text = $"{music.BitRate}kbps";
            SampleRateTextBlock.Text = $"{music.SampleRate}Hz";
            YearTextBlock.Value = music.Year;
            LastFolderNameTextBlock.Text = music.LastLevelFolderPath;
            DiskNumberBox.Value = music.DiskNumber;
            PathTextBlock.Text = music.Path;
            albumCoverData = await ToolUtils.GetRawImage(music, true);
            AlbumCoverImage.Source = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
            CreateTimeBlock.Text = music.CreateTime.ToString();
            UpdateTimeBlock.Text = music.UpdateTime.ToString();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async void SaveToDataBaseButton_Click(object sender, RoutedEventArgs e)
        {
            var music = AppData.allSongs.AsValueEnumerable().Where(m => m.Id == musicDetail.Id).FirstOrDefault();
            if (music is not null)
            {
                try
                {
                    music.Title = TitleTextBlock.Text;
                    music.Author = AuthorTextBlock.Text;
                    music.Album = AlbumTextBlock.Text;
                    music.Year = (int)YearTextBlock.Value;
                    music.DiskNumber = (int)DiskNumberBox.Value;
                    music.TrackNumber = (int)TrackNumberBox.Value;
                    music.Lyrics = LyricsTextBox.Text;
                    await App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(music);
                    AppData.allSongs = await App.Services.GetRequiredService<MusicDatabaseService>().GetMusicListAsync();
                    MusicDetailChanged?.Invoke(this, music);
                }
                catch (Exception ex)
                {
                    notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                }
            }
            this.Close();
        }

        private async Task UpdateFile(Music music, DateTime updateTime)
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
            music.UpdateTime = updateTime;
            await App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(music);
            if (AppData.albumCoverCache.ContainsKey(music.Album))
            {
                AppData.albumCoverCache[music.Album] = (BitmapImage)AlbumCoverImage.Source;
            }
            AppData.allSongs = await App.Services.GetRequiredService<MusicDatabaseService>().GetMusicListAsync();
            MusicDetailChanged?.Invoke(this, music);
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
            var music = AppData.allSongs.AsValueEnumerable().Where(m => m.Id == musicDetail.Id).FirstOrDefault();
            if (music is not null)
            {
                try
                {
                    DateTime newModificationTime = DateTime.Now;
                    await UpdateFile(music, newModificationTime);
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

        private async void GetLyricsFromNet_Click(object sender, RoutedEventArgs e)
        {
            (string lyrics, string transLrc)= await ToolUtils.GetLyricsFromNet(musicDetail);
            if (lyrics is not null)
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
            _ = Task.Run(() =>
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

        private void ReadLyricsFromFile_Click(object sender, RoutedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                StorageFile storageFile = await StorageFile.GetFileFromPathAsync(musicDetail.Path);
                Music music = await ToolUtils.GetMusicInfo(storageFile);
                if (music is not null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        TitleTextBlock.Text = music.Title;
                        AuthorTextBlock.Text = music.Author;
                        AlbumTextBlock.Text = music.Album;
                        if (!string.IsNullOrEmpty(music.Lyrics))
                        {
                            LyricsTextBox.Text = music.Lyrics;
                        }
                        TrackNumberBox.Value = music.TrackNumber;
                        DurationTextBlock.Text = music.Duration.ToString(@"mm\:ss");
                        BitDepthTextBlock.Text = $"{music.BitDepth}bit";
                        BitRateTextBlock.Text = $"{music.BitRate}kbps";
                        SampleRateTextBlock.Text = $"{music.SampleRate}Hz";
                        YearTextBlock.Value = music.Year;
                        LastFolderNameTextBlock.Text = music.LastLevelFolderPath;
                        DiskNumberBox.Value = music.DiskNumber;
                        PathTextBlock.Text = music.Path;
                        CreateTimeBlock.Text = music.CreateTime.ToString();
                        UpdateTimeBlock.Text = music.UpdateTime.ToString();
                    });
                }
            });
        }

        private async void SelectCoverImageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker openPicker = new FileOpenPicker(App.MainWindow.AppWindow.Id);
                openPicker.ViewMode = PickerViewMode.Thumbnail;
                openPicker.FileTypeFilter.Add(".jpg");
                openPicker.FileTypeFilter.Add(".jpeg");
                openPicker.FileTypeFilter.Add(".png");
                var file = await openPicker.PickSingleFileAsync();
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
