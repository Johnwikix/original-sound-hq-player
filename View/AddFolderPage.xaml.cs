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
using Windows.Storage;
using System.Reflection;
using Windows.System;

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
            await dbConnection.CreateTableAsync<Folder>();
            await dbConnection.CreateTableAsync<Music>();
            await LoadFoldersAsync();
        }

        private async Task LoadFoldersAsync()
        {
            try
            {
                var folderList = await dbConnection.Table<Folder>().ToListAsync();
                FolderListView.ItemsSource = folderList;
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
            }
        }

        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e){ // This is the correct method signature
            var button = sender as Button;
            if (button != null && button.Tag is int folderId)
            {
                // 获取要打开的文件夹信息
                var folderToOpen = await dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
                if (folderToOpen != null)
                {
                    // 打开文件夹
                    var folder = await StorageFolder.GetFolderFromPathAsync(folderToOpen.Path);
                    var options = new FolderLauncherOptions
                    {
                        DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
                    };
                    await Launcher.LaunchFolderAsync(folder, options);
                }
            }
        }
       

        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();
            folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            folderPicker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                // 存储文件夹信息到数据库
                var newFolder = new Folder
                {
                    Name = folder.Name,
                    Path = folder.Path,
                    Type = "本地"
                };
                await dbConnection.InsertAsync(newFolder);

                // 扫描文件夹中的音乐文件
                await ScanFolderAsync(folder);

                // 重新加载文件夹列表
                await LoadFoldersAsync();
            }
        }

        private async Task ScanFolderAsync(StorageFolder folder)
        {
            var files = await folder.GetFilesAsync();
            var musicFiles = new List<Music>();

            foreach (var file in files)
            {
                if (IsMusicFile(file.FileType))
                {
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
                        Album = musicProperties.Album,
                        FolderPath = folder.Path
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

        private async void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is int folderId)
            {
                // 获取要移除的文件夹信息
                var folderToRemove = await dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
                if (folderToRemove != null)
                {
                    // 移除该文件夹下的所有音乐文件
                    var musicFilesToRemove = await dbConnection.Table<Music>().Where(m => m.FolderPath == folderToRemove.Path).ToListAsync();
                    foreach (var musicFile in musicFilesToRemove)
                    {
                        await dbConnection.DeleteAsync(musicFile);
                    }
                    // 移除文件夹信息
                    await dbConnection.DeleteAsync(folderToRemove);

                    // 重新加载文件夹列表
                    await LoadFoldersAsync();
                }
            }
        }
    }
}
