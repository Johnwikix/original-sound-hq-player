using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TagLib;
using Windows.Storage;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.OnlineAPIs.CloudMusicAPI;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;
using ZLinq;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicDetailsWindow : WinUIEx.WindowEx,INotifyPropertyChanged
    {
        private Music MusicDetail
        {
            get;
            set
            {
                if (field != value)
                {
                    field = value;
                    OnPropertyChanged();
                }
            }
        }       
        public BitmapImage AlbumCoverBitmap
        {
            get;
            set
            {
                if (field != value)
                {
                    field = value;
                    OnPropertyChanged();
                }
            }
        }
        public bool IsLoading
        {
            get;
            set
            {
                if (field != value)
                {
                    field = value;
                    OnPropertyChanged();
                }
            }
        } = false;
        private NotificationService NotificationService { get; set; }
        private byte[] AlbumCoverData { get; set; } = null;
        private nint hwnd;
        private ThemeStyleHelper themeStyleHelper;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MusicDetailsWindow(Music music)
        {
            this.InitializeComponent();
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            this.SetTitleBarBackgroundColors(Colors.Transparent);
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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
            NotificationService = App.Services.GetRequiredService<NotificationService>();
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
            MusicDetail = music;  
            AlbumCoverData = await ToolUtils.GetRawImage(music, true);
            DispatcherQueue.TryEnqueue(async () => {
                AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(AlbumCoverData);
            });
            
        }

        private string ConvertDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return duration.ToString(@"hh\:mm\:ss");
            }
            else
            {
                return duration.ToString(@"mm\:ss");
            }
        }
        private string ConvertBitDepth(int bitDepth)
        {
            return $"{bitDepth}bit";
        }

        private string ConvertSampleRate(int sampleRate)
        {
            return $"{sampleRate}Hz";
        }

        private string ConvertBitRate(int bitRate)
        {
            return $"{bitRate}Kbps";
        }

        private string ConvertTime(DateTime time) {
            return time.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private Visibility BoolToVisibility(bool isLoading) {
            return isLoading ? Visibility.Visible : Visibility.Collapsed;
        }

        private Visibility BoolToNVisibility(bool isLoading)
        {
            return isLoading ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void SaveToDataBaseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(MusicDetail);
            }
            catch (Exception ex)
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
            this.Close();
        }

        private async Task UpdateFile(DateTime updateTime)
        {
            using (TagLib.File audioFile = TagLib.File.Create(MusicDetail.Path))
            {
                Tag tag = audioFile.Tag;
                tag.Pictures = Array.Empty<IPicture>();
                byte[] albumArtData = AlbumCoverData;
                Picture albumArt = new Picture
                {
                    Type = PictureType.FrontCover,
                    MimeType = System.Net.Mime.MediaTypeNames.Image.Jpeg,
                    Description = "Album Art",
                    Data = new ByteVector(albumArtData)
                };
                tag.Pictures = new IPicture[] { albumArt };
                tag.Title = MusicDetail.Title;
                tag.Album = MusicDetail.Album;
                tag.Performers = new string[] { MusicDetail.Author };
                tag.Track = (uint)MusicDetail.TrackNumber;
                tag.Disc = (uint)MusicDetail.DiskNumber;
                tag.Year = (uint)MusicDetail.Year;
                tag.Lyrics = MusicDetail.Lyrics;
                IsLoading = true;
                await Task.Run(() => audioFile.Save());
            }
            MusicDetail.UpdateTime = updateTime;
            await App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(MusicDetail);
            if (AppData.albumCoverCache.ContainsKey(MusicDetail.Album))
            {
                AppData.albumCoverCache[MusicDetail.Album] = AlbumCoverBitmap;
            }
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
            try
            {
                DateTime newModificationTime = DateTime.Now;
                await UpdateFile(newModificationTime);
            }
            catch (Exception ex)
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
            this.Close();
        }

        private async void GetImageFromNet_Click(object sender, RoutedEventArgs e)
        {
            AlbumCoverData = await App.Services.GetRequiredService<LrcService>().GetMixedCoverImageAsync(MusicDetail);
            if (AlbumCoverData is not null)
            {
                DispatcherQueue.TryEnqueue(async () => {
                    AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(AlbumCoverData);
                });                
            }
            else
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("FailedObtainCover"));
            }
        }

        private async void GetLyricsFromNet_Click(object sender, RoutedEventArgs e)
        {
            (string lyrics, string transLrc)= await ToolUtils.GetLyricsFromNet(MusicDetail);
            (string krc,string tKrc) = await ToolUtils.GetKrcFromNet(MusicDetail);
            MusicDetail.Lyrics = lyrics ?? string.Empty;
            MusicDetail.TranslatedLyrics = transLrc ?? string.Empty;
            MusicDetail.Krc = krc ?? string.Empty;
            MusicDetail.TKrc = tKrc ?? string.Empty;
            if (string.IsNullOrEmpty(lyrics) && string.IsNullOrEmpty(transLrc) && string.IsNullOrEmpty(krc) && string.IsNullOrEmpty(tKrc))
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("FailedObtainLyrics"));
            }
        }

        private async void SaveLyrics_Click(object sender, RoutedEventArgs e)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitizedFileName = Path.GetFileNameWithoutExtension(MusicDetail.Path);
            string? targetBasePath = Path.GetDirectoryName(MusicDetail.Path);
            if (targetBasePath is null) { return; }
            if (!string.IsNullOrEmpty(MusicDetail.Krc))
            {
                _ = Task.Run(() =>
                {
                    string lrcFileName = Path.ChangeExtension(sanitizedFileName, ".lrc");
                    string lrcFilePath = Path.Combine(targetBasePath, lrcFileName);
                    System.IO.File.WriteAllText(lrcFilePath, ToolUtils.ConvertLyrics(MusicDetail.Krc));
                    ToolUtils.OpenFileInExplorer(lrcFilePath);
                });
                if (!string.IsNullOrEmpty(MusicDetail.TKrc))
                {
                    _ = Task.Run(() =>
                    {
                        string newFileName = $"{sanitizedFileName}_Translated.lrc";
                        string lrcFilePath = Path.Combine(targetBasePath, newFileName);
                        System.IO.File.WriteAllText(lrcFilePath, ToolUtils.ConvertLyrics(MusicDetail.TKrc));
                        ToolUtils.OpenFileInExplorer(lrcFilePath);
                    });
                }
                return;
            }
            if (!string.IsNullOrEmpty(MusicDetail.Lyrics)) {
                _ = Task.Run(() =>
                {
                    string lrcFileName = Path.ChangeExtension(sanitizedFileName, ".lrc");
                    string lrcFilePath = Path.Combine(targetBasePath, lrcFileName);
                    System.IO.File.WriteAllText(lrcFilePath, ToolUtils.ConvertLyrics(MusicDetail.Lyrics));
                    ToolUtils.OpenFileInExplorer(lrcFilePath);
                });
                if (!string.IsNullOrEmpty(MusicDetail.TranslatedLyrics))
                {
                    _ = Task.Run(() =>
                    {
                        string newFileName = $"{sanitizedFileName}_Translated.lrc";
                        string lrcFilePath = Path.Combine(targetBasePath, newFileName);
                        System.IO.File.WriteAllText(lrcFilePath, ToolUtils.ConvertLyrics(MusicDetail.TranslatedLyrics));
                        ToolUtils.OpenFileInExplorer(lrcFilePath);
                    });
                }
            }            
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            ToolUtils.OpenFileInExplorer(MusicDetail.Path);
        }

        private void ReadLyricsFromFile_Click(object sender, RoutedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                StorageFile storageFile = await StorageFile.GetFileFromPathAsync(MusicDetail.Path);
                Music music = await ToolUtils.GetMusicInfo(storageFile);
                if (music is not null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        MusicDetail.Title = music.Title;
                        MusicDetail.Author = music.Author;
                        MusicDetail.Album = music.Album;
                        MusicDetail.Lyrics = music.Lyrics;
                        MusicDetail.TrackNumber = music.TrackNumber;
                        MusicDetail.Duration = music.Duration;
                        MusicDetail.BitDepth = music.BitDepth;
                        MusicDetail.BitRate = music.BitRate;
                        MusicDetail.SampleRate = music.SampleRate;
                        MusicDetail.Year = music.Year;
                        MusicDetail.LastLevelFolderPath = music.LastLevelFolderPath;
                        MusicDetail.DiskNumber = music.DiskNumber;
                        MusicDetail.Path = music.Path;
                        MusicDetail.CreateTime = music.CreateTime;
                        MusicDetail.UpdateTime = music.UpdateTime;
                    });
                }
            });
        }

        private async void SelectCoverImageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker openPicker = new(App.MainWindow.AppWindow.Id)
                {
                    ViewMode = PickerViewMode.Thumbnail
                };
                openPicker.FileTypeFilter.Add(".jpg");
                openPicker.FileTypeFilter.Add(".jpeg");
                openPicker.FileTypeFilter.Add(".png");
                var file = await openPicker.PickSingleFileAsync();
                if (file is not null)
                {
                    AlbumCoverData = await System.IO.File.ReadAllBytesAsync(file.Path);
                    DispatcherQueue.TryEnqueue(async () => {
                        AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(AlbumCoverData);
                    });
                }
            }
            catch (Exception ex)
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
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
                    await System.IO.File.WriteAllBytesAsync(file.Path, AlbumCoverData);
                }
                catch (Exception ex)
                {
                    NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                }
            }
        }

        private void CloseFlyoutButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
        }
    }
}
