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
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.Storage;
using static System.Windows.Forms.DataFormats;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices;
using WinUIMusicPlayer.WebService;

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
            appWindow.SetIcon("Assets/icon.ico");
            uint dpi = GetDpiForWindow(hwnd);
            double scaleFactor = dpi / 96.0;
            int originalWidth = 650;
            int originalHeight = 650;
            int adjustedWidth = (int)(originalWidth * scaleFactor);
            int adjustedHeight = (int)(originalHeight * scaleFactor);
            appWindow.MoveAndResize(new RectInt32(_X: 560, _Y: 280, _Width: adjustedWidth, _Height: adjustedHeight));
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
            BitDepthTextBlock.Text = $"{music.BitDepth}bit" ;
            BitRateTextBlock.Text = $"{music.BitRate}kbps";
            SampleRateTextBlock.Text = $"{music.SampleRate}Hz";
            YearTextBlock.Text = music.Year.ToString();
            PathTextBlock.Text = music.Path;
            AlbumCoverImage.Source =await ToolUtils.GetImageFromMusic(music,150);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async Task UpdateFile(Music music) {
            Track theTrack = new Track(musicDetail.Path);
            theTrack.Title = TitleTextBlock.Text;
            theTrack.Artist = AuthorTextBlock.Text;
            theTrack.Album = AlbumTextBlock.Text;
            theTrack.Year = int.Parse(YearTextBlock.Text);
            theTrack.TrackNumber = int.Parse(TrackNumberBox.Text);
            theTrack.Lyrics.Clear();
            theTrack.Lyrics.ParseLRC(LyricsTextBox.Text);
            var oldpic = theTrack.EmbeddedPictures.FirstOrDefault();
            if (oldpic != null)
            {
                theTrack.EmbeddedPictures.Remove(oldpic);
            }
            var imageByte = await ToolUtils.ImageToByteArray(AlbumCoverImage);
            PictureInfo newPicture = PictureInfo.fromBinaryData(imageByte, PictureInfo.PIC_TYPE.CD);
            theTrack.EmbeddedPictures.Add(newPicture);
            await theTrack.SaveAsync();
            music.Title = TitleTextBlock.Text;
            music.Author = AuthorTextBlock.Text;
            music.Album = AlbumTextBlock.Text;
            music.Year = int.Parse(YearTextBlock.Text);
            music.TrackNumber = int.Parse(TrackNumberBox.Text);
            music.Lyrics = LyricsTextBox.Text;
            await MusicDatabaseService.UpdateMusicInfo(music);
            MusicDetailChanged?.Invoke(this, music);
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
                LoadingGrid.Visibility = Visibility.Visible;
                var music = await MusicDatabaseService.GetMusic(musicDetail.Id);
                if (music != null)
                {
                    try
                    {
                        await UpdateFile(music);
                    }
                    catch (Exception ex)
                    {
                        ContentDialog errorDialog = new ContentDialog
                        {
                            Title = "错误",
                            Content = ex.Message,
                            PrimaryButtonText = "确定",
                            CloseButtonText = "取消",
                            XamlRoot = this.Content.XamlRoot
                        };
                        ContentDialogResult errorResult = await errorDialog.ShowAsync();
                    }                   
                }
                this.Close();
            }            
        }

        private async void GetImageFromNet_Click(object sender, RoutedEventArgs e)
        {
            LrcService lrcService = new LrcService();
            BitmapImage bitmapImage = await lrcService.GetCoverImageAsync(musicDetail.Title, musicDetail.Album, musicDetail.Author);
            if (bitmapImage != null)
            {
                AlbumCoverImage.Source = bitmapImage;
            }
            else {
                ContentDialog errorDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = "获取封面失败，请检查设置",
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    XamlRoot = this.Content.XamlRoot
                };
                ContentDialogResult errorResult = await errorDialog.ShowAsync();
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
            else {
                ContentDialog errorDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = "获取歌词失败，请检查设置",
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    XamlRoot = this.Content.XamlRoot
                };
                ContentDialogResult errorResult = await errorDialog.ShowAsync();
            }
        }

        private async void SelectCoverImageButton_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker openPicker = new FileOpenPicker();
            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            openPicker.FileTypeFilter.Add(".jpg");
            openPicker.FileTypeFilter.Add(".jpeg");
            openPicker.FileTypeFilter.Add(".png");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);
            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null)
            {
                Track theTrack = new Track(musicDetail.Path);
                var oldpic = theTrack.EmbeddedPictures.FirstOrDefault();
                if (oldpic != null) {
                    theTrack.EmbeddedPictures.Remove(oldpic);
                }
                PictureInfo newPicture = PictureInfo.fromBinaryData(System.IO.File.ReadAllBytes(file.Path), PictureInfo.PIC_TYPE.CD);
                theTrack.EmbeddedPictures.Add(newPicture);
                _=theTrack.SaveAsync();
                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    BitmapImage bitmapImage = new BitmapImage();
                    await bitmapImage.SetSourceAsync(stream);
                    AlbumCoverImage.Source = bitmapImage;
                }
            }
        }

        private async void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (AlbumCoverImage.Source is BitmapImage bitmapImage)
            {
                var renderTargetBitmap = new RenderTargetBitmap();
                // 设置渲染尺寸为图片的原始尺寸
                await renderTargetBitmap.RenderAsync(AlbumCoverImage, (int)bitmapImage.PixelWidth, (int)bitmapImage.PixelHeight);
                var pixelBuffer = await renderTargetBitmap.GetPixelsAsync();
                var pixels = pixelBuffer.ToArray();
                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeChoices.Add("PNG Image", new[] { ".png" });
                picker.SuggestedFileName = "SavedImage";

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                StorageFile file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    using (var stream = await file.OpenStreamForWriteAsync())
                    {
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
                        encoder.SetPixelData(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied,
                            (uint)renderTargetBitmap.PixelWidth,
                            (uint)renderTargetBitmap.PixelHeight,
                            96,
                            96,
                            pixels);
                        await encoder.FlushAsync();
                    }
                }
            }
        }
    }
}
