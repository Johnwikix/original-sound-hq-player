using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using TagLib;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.WebService;
using TagLib;

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
        public MusicDetailsWindow(Music music)
        {
            this.InitializeComponent();
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(MusicDetailTitleBar);
            setWindow();
            InitalizeData(music);

        }

        private void setWindow()
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            appWindow.SetIcon("Assets/icon.ico");
            uint dpi = GetDpiForWindow(hwnd);
            scaleFactor = dpi / 96.0;
            int originalWidth = 650;
            int originalHeight = 650;
            int adjustedWidth = (int)(originalWidth * scaleFactor);
            int adjustedHeight = (int)(originalHeight * scaleFactor);
            appWindow.MoveAndResize(new RectInt32(_X: 560, _Y: 280, _Width: adjustedWidth, _Height: adjustedHeight));
            notificationService = new NotificationService();
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
            PathTextBlock.Text = music.Path;
            albumCoverData = ToolUtils.GetRawImage(music);
            AlbumCoverImage.Source = await ToolUtils.ConvertByteArrayToBitmapImage(albumCoverData);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async Task UpdateFile(Music music) {
            using (TagLib.File audioFile = TagLib.File.Create(music.Path))
            {
                // 读取专辑图片数据
                byte[] albumArtData = await ToolUtils.ImageToByteArray(AlbumCoverImage);
                // 创建 Picture 对象
                Picture albumArt = new Picture
                {
                    Type = PictureType.FrontCover,
                    MimeType = System.Net.Mime.MediaTypeNames.Image.Jpeg,
                    Description = "Album Art",
                    Data = new ByteVector(albumArtData)
                };

                // 获取音频文件的标签
                Tag tag = audioFile.Tag;

                // 设置专辑图片
                tag.Pictures = new IPicture[] { albumArt };

                // 设置其他信息
                tag.Title = TitleTextBlock.Text;
                tag.Album = AlbumTextBlock.Text;
                tag.Performers = new string[] { AuthorTextBlock.Text };
                tag.Track = uint.Parse(TrackNumberBox.Text); // 假设音轨编号为 5
                tag.Year = uint.Parse(YearTextBlock.Text); // 假设年份为 2023
                tag.Lyrics = LyricsTextBox.Text;
                // 保存更改
                LoadingGrid.Visibility = Visibility.Visible;
                MusicDetail.Visibility = Visibility.Collapsed;
                audioFile.Save();
                Console.WriteLine("所有信息已成功写入。");
            }
            //Track theTrack = new Track(musicDetail.Path);
            //theTrack.Title = TitleTextBlock.Text;
            //theTrack.Artist = AuthorTextBlock.Text;
            //theTrack.Album = AlbumTextBlock.Text;
            //theTrack.Year = int.Parse(YearTextBlock.Text);
            //theTrack.TrackNumber = int.Parse(TrackNumberBox.Text);
            //theTrack.Lyrics.Clear();
            //theTrack.Lyrics.ParseLRC(LyricsTextBox.Text);
            //var oldpic = theTrack.EmbeddedPictures.FirstOrDefault();
            //if (oldpic != null)
            //{
            //    theTrack.EmbeddedPictures.Remove(oldpic);
            //}
            //var imageByte = await ToolUtils.ImageToByteArray(AlbumCoverImage);
            //PictureInfo newPicture = PictureInfo.fromBinaryData(imageByte, PictureInfo.PIC_TYPE.CD);
            //theTrack.EmbeddedPictures.Add(newPicture);            
            //await theTrack.SaveAsync();
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

            var music = await MusicDatabaseService.GetMusic(musicDetail.Id);
            if (music != null)
            {
                try
                {
                    await UpdateFile(music);
                }
                catch (Exception ex)
                {
                    notificationService.SendNotification("错误", ex.Message);
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
                notificationService.SendNotification("错误", "获取封面失败，请检查设置");
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
                notificationService.SendNotification("错误", "获取歌词失败，请检查设置");
            }
        }

        private async void SelectCoverImageButton_Click(object sender, RoutedEventArgs e)
        {
            try {
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
            } catch (Exception ex) {
                notificationService.SendNotification("错误", ex.Message);
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
                    notificationService.SendNotification("错误", ex.Message);
                }
            }
        }

        private void CloseFlyoutButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmFlyout.Hide();
        }
    }
}
