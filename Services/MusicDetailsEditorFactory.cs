using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;

namespace WinUIMusicPlayer.Services;

/// <summary>集中延迟解析仅在属性打开时需要的编辑依赖，避免首页启动网络与歌词编辑器。</summary>
public sealed class MusicDetailsEditorFactory(IServiceProvider services) : IMusicDetailsEditorFactory
{
    /// <inheritdoc/>
    public MusicDetailsEditorViewModel Create(Music music)
    {
        var database = services.GetRequiredService<MusicDatabaseService>();
        var lyrics = new LyricsEditorViewModel(music, database, services.GetRequiredService<LyricsParser>(),
            services.GetRequiredService<LyricsOnlineSearch>(), services.GetRequiredService<ApplicationTasks>(),
            services.GetRequiredService<ILlmTranslationService>());
        return new MusicDetailsEditorViewModel(music, database, lyrics, services.GetRequiredService<EditorSessions>(),
            new EditorHost(services), services.GetRequiredService<ILogger<MusicDetailsEditorViewModel>>());
    }
    private sealed class EditorHost(IServiceProvider services) : IMusicDetailsEditorHost
    {
        public async Task<byte[]?> ReadCoverAsync(Music music, CancellationToken token)
        {
            var bytes = await Task.Run(() => ToolUtils.GetRawImage(music, true), token);
            token.ThrowIfCancellationRequested();
            return bytes;
        }
        public async Task<byte[]?> FindCoverAsync(Music music, CancellationToken token)
        {
            var bytes = await services.GetRequiredService<LrcService>().GetMixedCoverImageAsync(music);
            token.ThrowIfCancellationRequested();
            return bytes;
        }
        public async Task<byte[]?> ChooseCoverAsync(CancellationToken token)
        {
            var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id) { ViewMode = PickerViewMode.Thumbnail };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            var file = await picker.PickSingleFileAsync();
            token.ThrowIfCancellationRequested();
            return file is null ? null : await File.ReadAllBytesAsync(file.Path, token);
        }
        public async Task ExportCoverAsync(byte[] cover, CancellationToken token)
        {
            var picker = new FileSavePicker(App.MainWindow.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = "SavedImage" };
            picker.FileTypeChoices.Add("JPEG Image", new[] { ".jpg" });
            var file = await picker.PickSaveFileAsync();
            token.ThrowIfCancellationRequested();
            if (file is not null) await File.WriteAllBytesAsync(file.Path, cover, token);
        }
        public void OpenLocation(Music music) => ToolUtils.OpenFileInExplorer(music.Path);
        public void MetadataChanged()
        {
            services.GetRequiredService<AppViewModel>().NotifySongsSourceChanged();
            services.GetRequiredService<LibraryProjectionService>().RequestRefresh();
        }
        public void NotifyWriteQueued(Music music) => services.GetRequiredService<NotificationService>()
            .SendNotification(music.Title, ToolUtils.GetString("MetadataWriteQueued"));
    }
}
