using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
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

            var mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.FoldersLoaded += MainWindow_FoldersLoaded;
            }
        }

        private void MainWindow_FoldersLoaded(object sender, IEnumerable<Folder> folderList)
        {
            try
            {
                FolderListView.ItemsSource = folderList;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新文件夹列表时出错: {ex.Message}");
            }
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Folder>();
            await dbConnection.CreateTableAsync<Music>();
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

        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        { // This is the correct method signature
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
                // 检查是否已存在包含或被包含的文件夹
                var existingFolders = await dbConnection.Table<Folder>().ToListAsync();

                // 检查新添加的文件夹是否已经在已存在的文件夹中
                bool folderAlreadyExists = existingFolders.Any(f =>
                    folder.Path.StartsWith(f.Path) || f.Path.StartsWith(folder.Path));

                if (!folderAlreadyExists)
                {
                    // 移除被新文件夹包含的旧文件夹
                    var foldersToRemove = existingFolders
                        .Where(f => folder.Path.StartsWith(f.Path))
                        .ToList();

                    foreach (var folderToRemove in foldersToRemove)
                    {
                        // 删除该文件夹及其音乐文件
                        var musicFilesToRemove = await dbConnection.Table<Music>()
                            .Where(m => m.FolderPath.StartsWith(folderToRemove.Path))
                            .ToListAsync();

                        foreach (var musicFile in musicFilesToRemove)
                        {
                            await dbConnection.DeleteAsync(musicFile);
                        }

                        await dbConnection.DeleteAsync(folderToRemove);
                    }

                    // 存储新文件夹信息到数据库
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
                    var mainWindow = (App.MainWindow as MainWindow);
                    if (mainWindow != null)
                    {
                        await mainWindow.LoadMusicList();
                    }
                }
                else
                {
                    // 可以添加一个提示，告诉用户文件夹已经存在或被包含
                }
            }
        }

        private async Task ScanFolderAsync(StorageFolder folder)
        {
            var musicFiles = new List<Music>();
            // 递归获取所有音乐文件
            await GetMusicFilesRecursive(folder, musicFiles);

            // 获取已存在的音乐文件路径
            var existingMusicPaths = await dbConnection.Table<Music>()
                .ToListAsync()
                .ContinueWith(t => t.Result.Select(m => m.Path).ToList());

            // 过滤掉已存在的音乐文件
            var newMusicFiles = musicFiles
                .Where(m => !existingMusicPaths.Contains(m.Path))
                .ToList();

            // 只插入新的音乐文件
            if (newMusicFiles.Any())
            {
                await dbConnection.InsertAllAsync(newMusicFiles);
            }
        }

        private async Task GetMusicFilesRecursive(StorageFolder folder, List<Music> musicFiles)
        {
            var files = await folder.GetFilesAsync();

            foreach (var file in files)
            {
                if (IsMusicFile(file.FileType))
                {
                    // 获取基本音乐属性
                    var musicProperties = await file.Properties.GetMusicPropertiesAsync();
                    int trackNumber = (int)musicProperties.TrackNumber;
                    string title = string.IsNullOrEmpty(musicProperties.Title) ?
                        Path.GetFileNameWithoutExtension(file.Name) : musicProperties.Title;

                    // 获取最后一级目录名
                    string lastLevelFolderPath = Path.GetFileName(folder.Path);

                    // 默认值
                    int sampleRate = 0;
                    int channelCount = 0;
                    int bitDepth = 0;
                    int bitRate = 0;                    

                    // 尝试获取详细的音频属性
                    try
                    {
                        // 使用RetrievePropertiesAsync获取详细音频信息
                        var audioProps = await file.Properties.RetrievePropertiesAsync(new string[] {
                            "System.Audio.SampleRate",
                            "System.Audio.ChannelCount",
                            "System.Audio.EncodingBitrate",
                            "System.Audio.SampleSize"
                        });
                        // 处理采样率
                        if (audioProps.ContainsKey("System.Audio.SampleRate") && audioProps["System.Audio.SampleRate"] != null)
                        {
                            sampleRate = Convert.ToInt32(audioProps["System.Audio.SampleRate"]);
                        }

                        // 处理声道数
                        if (audioProps.ContainsKey("System.Audio.ChannelCount") && audioProps["System.Audio.ChannelCount"] != null)
                        {
                            channelCount = Convert.ToInt32(audioProps["System.Audio.ChannelCount"]);
                        }

                        // 处理比特率
                        if (audioProps.ContainsKey("System.Audio.EncodingBitrate") && audioProps["System.Audio.EncodingBitrate"] != null)
                        {
                            int rawBitrate = Convert.ToInt32(audioProps["System.Audio.EncodingBitrate"]);
                            bitRate = rawBitrate > 0 ? rawBitrate/1000 : 0;
                        }

                        // 处理位深度
                        if (audioProps.ContainsKey("System.Audio.SampleSize") && audioProps["System.Audio.SampleSize"] != null)
                        {
                            var sampleSize = Convert.ToInt32(audioProps["System.Audio.SampleSize"]);
                            bitDepth = sampleSize;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"获取音频属性时出错: {ex.Message}");
                    }

                    var music = new Music
                    {
                        Path = file.Path,
                        Title = title,
                        Author = string.IsNullOrEmpty(musicProperties.Artist)? "未知艺术家": musicProperties.Artist,
                        Duration = musicProperties.Duration,
                        Album =  string.IsNullOrEmpty(musicProperties.Album) ? "未知专辑": musicProperties.Album,
                        FolderPath = folder.Path,
                        LastLevelFolderPath = lastLevelFolderPath,
                        Extension = file.FileType.TrimStart('.').ToUpper(),
                        BitDepth = bitDepth,
                        BitRate = bitRate,
                        SampleRate = sampleRate,
                        Channel = channelCount,
                        TrackNumber = trackNumber,
                    };
                    musicFiles.Add(music);
                }
            }

            // 递归扫描子文件夹
            var subfolders = await folder.GetFoldersAsync();
            foreach (var subfolder in subfolders)
            {
                await GetMusicFilesRecursive(subfolder, musicFiles);
            }
        }

        private bool IsMusicFile(string fileType)
        {
            var musicExtensions = new[] { ".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg", "m4a"};
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
                    // 删除该文件夹及其所有子文件夹下的音乐文件
                    var musicFilesToRemove = await dbConnection.Table<Music>()
                        .Where(m => m.FolderPath.StartsWith(folderToRemove.Path))
                        .ToListAsync();

                    foreach (var musicFile in musicFilesToRemove)
                    {
                        await dbConnection.DeleteAsync(musicFile);
                    }

                    // 移除文件夹信息
                    await dbConnection.DeleteAsync(folderToRemove);

                    // 重新加载文件夹列表
                    await LoadFoldersAsync();
                    var mainWindow = (App.MainWindow as MainWindow);
                    if (mainWindow != null)
                    {
                        await mainWindow.LoadMusicList();
                    }
                }
            }
        }
    }
}
