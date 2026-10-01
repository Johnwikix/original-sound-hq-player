using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using Windows.Storage;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.WebService;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.Services.Lyrics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicDetailsWindow : WinUIEx.WindowEx, INotifyPropertyChanged
    {
        private static ILogger<MusicDetailsWindow> _logger = App.GetLogger<MusicDetailsWindow>();
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
        public BitmapImage? AlbumCoverBitmap
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
        public LyricsEditorViewModel LyricsEditor { get; }
        private NotificationService NotificationService { get; set; }
        private byte[] AlbumCoverData { get; set; } = null;
        private nint hwnd;
        private ThemeStyleHelper themeStyleHelper;
        private bool _closed;
        private IDisposable? _editorSession;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MusicDetailsWindow(Music music)
        {
            MusicDetail = music;
            LyricsEditor = new LyricsEditorViewModel(music, App.Services.GetRequiredService<MusicDatabaseService>(),
                App.Services.GetRequiredService<LyricsParser>(), App.Services.GetRequiredService<LyricsOnlineSearch>(),
                App.Services.GetRequiredService<ApplicationTasks>(), App.Services.GetRequiredService<ILlmTranslationService>());
            this.InitializeComponent();
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            this.SetTitleBarBackgroundColors(Colors.Transparent);
            SetTitleBar(MusicDetailTitleBar);
            setWindow();
            _editorSession = App.Services.GetRequiredService<EditorSessions>().Attach(() =>
            {
                LyricsEditor.Dispose();
                return Task.CompletedTask;
            });
            _ = App.Services.GetRequiredService<ApplicationTasks>().RunAsync(async token =>
            {
                try { await InitalizeData(music); }
                catch (Exception ex) { _logger.LogError(ex, "加载歌曲详情失败"); }
            });
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
            _closed = true;
            LyricsEditor.Dispose();
            _editorSession?.Dispose();
            this.Closed -= MusicDetailWindow_Closed;
        }

        private void setWindow()
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowSizeHelper.ResizeWindowAndCenterInMainWindow(hwnd, 850, 700, App.MainWindow.AppWindow, this.AppWindow);
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

        private async Task InitalizeData(Music music)
        {
            MusicDetail = music;
            await LyricsEditor.LoadAsync();
            AlbumCoverData = await Task.Run(() => ToolUtils.GetRawImage(music, true));
            if (_closed || Content is null) return;
            AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(AlbumCoverData);
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

        private string ConvertTime(DateTime time)
        {
            return time.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_closed) this.Close();
        }

        private Visibility BoolToVisibility(bool isLoading)
        {
            return isLoading ? Visibility.Visible : Visibility.Collapsed;
        }

        private Visibility BoolToNVisibility(bool isLoading)
        {
            return isLoading ? Visibility.Collapsed : Visibility.Visible;
        }

        private Visibility TextToVisibility(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void SaveToDataBaseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!await LyricsEditor.SaveAsync()) return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UpdateFile 更新文件失败: {ex.Message}");
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                return;
            }
            if (!_closed) this.Close();
        }

        private async Task<bool> UpdateFile(DateTime updateTime)
        {
            IsLoading = true;
            MusicDetail.UpdateTime = updateTime;
            bool saved = await LyricsEditor.SaveAsync(AlbumCoverData, queueMetadata: true);
            if (saved) NotificationService.SendNotification(MusicDetail.Title, ToolUtils.GetString("MetadataWriteQueued"));
            return saved;
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (MusicDetail.IsRemote) return;
            ConfirmFlyout.Hide();
            try
            {
                DateTime newModificationTime = DateTime.Now;
                if (await UpdateFile(newModificationTime) && !_closed) this.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SaveToDataBaseButton_Click 保存数据库信息失败: {ex.Message}");
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
            finally { IsLoading = false; }
        }

        private async void GetImageFromNet_Click(object sender, RoutedEventArgs e)
        {
            AlbumCoverData = await App.Services.GetRequiredService<LrcService>().GetMixedCoverImageAsync(MusicDetail);
            if (AlbumCoverData is not null)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(AlbumCoverData);
                });
            }
            else
            {
                NotificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("FailedObtainCover"));
            }
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            ToolUtils.OpenFileInExplorer(MusicDetail.Path);
        }

        private async void ReadLyricsFromFile_Click(object sender, RoutedEventArgs e)
        {
            if (!MusicDetail.IsRemote) await LyricsEditor.ReadEmbeddedAsync();
        }

        private async void SelectCoverImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (MusicDetail.IsRemote) return;
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
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        AlbumCoverBitmap = await ToolUtils.ConvertByteArrayToBitmapImage(AlbumCoverData);
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SelectCoverImageButton_Click 选择封面图片失败: {ex.Message}");
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
                    _logger.LogError(ex, $"SaveImageButton_Click 保存封面图片失败: {ex.Message}");
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
