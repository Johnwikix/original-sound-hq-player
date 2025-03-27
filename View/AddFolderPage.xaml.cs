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
using Microsoft.UI.Xaml.Controls;
using SQLite;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AddFolderPage : Page
    {
        private SQLiteAsyncConnection dbConnection;

        public AddFolderPage()
        {
            this.InitializeComponent();
            InitializeDatabase();
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
        }

        private async void ScanFolderButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();
            folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            folderPicker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                await ScanFolderAsync(folder);
            }
        }

        private async Task ScanFolderAsync(Windows.Storage.StorageFolder folder)
        {
            var files = await folder.GetFilesAsync();
            var musicFiles = new List<Music>();

            foreach (var file in files)
            {
                if (IsMusicFile(file.FileType))
                {
                    var existingMusic = await dbConnection.Table<Music>().Where(m => m.Path == file.Path).FirstOrDefaultAsync();
                    if (existingMusic != null)
                    {
                        continue; // 如果已存在，跳过该文件
                    }
                    var musicProperties = await file.Properties.GetMusicPropertiesAsync();
                    var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.MusicView);
                    byte[] coverBytes = null;
                    if (thumbnail != null)
                    {
                        using (var stream = thumbnail.AsStream())
                        {
                            coverBytes = new byte[stream.Length];
                            await stream.ReadAsync(coverBytes, 0, (int)stream.Length);
                        }
                    }
                    string title = string.IsNullOrEmpty(musicProperties.Title) ? Path.GetFileNameWithoutExtension(file.Name) : musicProperties.Title;
                    var music = new Music
                    {
                        Path = file.Path,
                        Title = title,
                        Cover = coverBytes,
                        Author = musicProperties.Artist,
                        Duration = musicProperties.Duration,
                        Album = musicProperties.Album
                    };
                    musicFiles.Add(music);
                }
            }

            await dbConnection.InsertAllAsync(musicFiles);
        }

        private bool IsMusicFile(string fileType)
        {
            var musicExtensions = new[] { ".mp3", ".wav", ".flac" };
            return musicExtensions.Contains(fileType.ToLower());
        }
    }
}
