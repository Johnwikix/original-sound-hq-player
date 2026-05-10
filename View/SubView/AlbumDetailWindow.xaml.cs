using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    public sealed partial class AlbumDetailWindow : WinUIEx.WindowEx, INotifyPropertyChanged
    {
        public Music MusicDetail
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
        private List<Music> AlbumMusics {get;set;}
        //public EventHandler<Music> AlbumDetailChanged;
        private NotificationService NotificationService { get; set;  }
        private byte[] albumCoverData = null;
        private nint hwnd;
        private ThemeStyleHelper themeStyleHelper;

        public event PropertyChangedEventHandler? PropertyChanged;

        public AlbumDetailWindow(Music music)
        {
            this.InitializeComponent();
            MusicDetail = music;
            AlbumMusics = App.Services.GetRequiredService<AppViewModel>().SongsSource.AsValueEnumerable().Where(m => m.Album == MusicDetail.Album).ToList();
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            this.SetTitleBarBackgroundColors(Colors.Transparent);
            SetTitleBar(AlbumDetailTitleBar);
            SetWindow();
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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

        private void SetWindow()
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowSizeHelper.ResizeWindowAndCenterInMainWindow(hwnd, 700, 550, App.MainWindow.AppWindow, this.AppWindow);
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

        private async void InitalizeData(Music album)
        {
            albumCoverData = await ToolUtils.GetRawImage(album, true);
            AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
        }

        private async void SaveToDataBaseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (Music music in AlbumMusics) {
                    music.Album = MusicDetail.Album;
                    music.Year = MusicDetail.Year;
                    await App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(music);
                }
                App.Services.GetRequiredService<AppViewModel>().RefreshDataSource();
            }
            catch (Exception ex)
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
            this.Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async Task UpdateFile()
        {
            LoadingGrid.Visibility = Visibility.Visible;
            AlbumDetail.Visibility = Visibility.Collapsed;
            // 避免重复写入标志位
            bool isResultAssigned = false;
            foreach (var music in AlbumMusics)
            {
                using (TagLib.File audioFile = TagLib.File.Create(music.Path))
                {
                    Tag tag = audioFile.Tag;
                    tag.Pictures = [];
                    if (albumCoverData is not null)
                    {
                        byte[] albumArtData = albumCoverData;
                        Picture albumArt = new()
                        {
                            Type = PictureType.FrontCover,
                            MimeType = System.Net.Mime.MediaTypeNames.Image.Jpeg,
                            Description = "Album Art",
                            Data = [.. albumArtData]
                        };
                        tag.Pictures = [albumArt];
                    }
                    tag.Album = music.Album;
                    tag.Year = (uint)music.Year;
                    await Task.Run(() => audioFile.Save());
                }
                await App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicInfo(music);
                if (!isResultAssigned)
                {
                    if (AppData.albumCoverCache.ContainsKey(music.Album))
                    {
                        AppData.albumCoverCache[music.Album] = AlbumCoverBitmap;
                    }
                    isResultAssigned = true;
                }
            }
            //App.Services.GetRequiredService<AppViewModel>().RefreshDataSource();
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
            try
            {
                await UpdateFile();
            }
            catch (Exception ex)
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
            this.Close();
        }

        private async void GetImageFromNet_Click(object sender, RoutedEventArgs e)
        {
            //if (string.IsNullOrEmpty(AppSettings.LrcAPISource) || AppSettings.LrcAPISource == "https://api.lrc.cx")
            //{
            //    albumCoverData = await CloudMusicSearchHelper.GetSongAlbum(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            //}
            //else
            //{
            //    albumCoverData = await LrcService.GetCoverImageAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            //}
            albumCoverData = await App.Services.GetRequiredService<LrcService>().GetMixedCoverImageAsync(MusicDetail);
            if (albumCoverData is not null)
            {
                AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
            }
            else
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("FailedObtainCover"));
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
                    AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
                }
            }
            catch (Exception ex)
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }

        private async void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker(App.MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
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
