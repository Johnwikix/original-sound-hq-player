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
using WinUIMusicPlayer.View;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.Utils;
using Windows.ApplicationModel;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.Gui;
using static SQLite.TableMapping;
using static WinUIMusicPlayer.Utils.ToolUtils;
using System.Data.Common;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using SQLite;
using NAudio.CoreAudioApi;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private AppWindow m_AppWindow;
        private SQLiteAsyncConnection dbConnection;
        public event EventHandler<IEnumerable<Folder>> FoldersLoaded;
        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            InitializeDatabase();
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
            await dbConnection.CreateTableAsync<Folder>();
            await dbConnection.CreateTableAsync<SavePlayState>();
            await dbConnection.CreateTableAsync<SaveSettings>();
            LoadState();
            LoadFoldersAsync();
        }
        private async Task LoadFoldersAsync()
        {
            try
            {
                var folderList = await dbConnection.Table<Folder>().ToListAsync();
                // 触发事件通知子页面更新
                FoldersLoaded?.Invoke(this, folderList);
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
            }
        }


        private async Task LoadState()
        {
            var playState = await dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
            if (playState == null)
            {
                // 如果没有记录，默认设置为列表循环
                playState = new SavePlayState
                {
                    PlayMode = PlayMode.ListLoop,
                    Volume = 0.5f,
                    LastPlayedMusicId = null
                };
                await dbConnection.InsertAsync(playState);
            }
            AppData.PlayMode = playState.PlayMode;
            AppData.LastPlayedMusicId = playState.LastPlayedMusicId;
            AppData.Volume = playState.Volume;
            var musicList = (await dbConnection.Table<Music>().ToListAsync()).OrderBy(m => m.Title).ToList();
            AppData.MusicList = musicList;
            var settings = await dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            if (settings != null)
            {
                AppSettings.OutputMode = settings.OutputMode;
                AppSettings.Latency = settings.Latency;
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    if (device.FriendlyName == settings.DeviceFriendlyName)
                    {
                        AppSettings.OutputDevice.mMDevice = device;
                        AppSettings.DeviceName = device.FriendlyName;
                        break;
                    }
                }
                foreach (var device in devices)
                {
                    AppSettings.outputDeviceList.Add(device.FriendlyName);
                }
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 移除事件处理程序，避免重复触发
            this.Activated -= MainWindow_Activated;
            // 初始导航到 AddFolder 页面
            NavigateToPage(typeof(AddFolderPage));
        }
        private void NavigateToPage(Type pageType)
        {
            ContentFrame.Navigate(pageType);
        }


        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                NavigateToPage(typeof(SettingsPage));
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        NavigateToPage(typeof(AddFolderPage));
                        break;
                    case "MusicBrowse":
                        NavigateToPage(typeof(MusicBrowsePage));
                        break;
                }
            }
        }
    }
}
