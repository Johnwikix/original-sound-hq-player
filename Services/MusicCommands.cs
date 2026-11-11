using AnimatedWin2dControls.Messages;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public static class MusicCommands
    {
        // 协调器用选曲代次取消旧请求；不能让共享命令在网络探活期间禁用所有歌曲行。
        public static IAsyncRelayCommand<Music> PlayCommand { get; } = new AsyncRelayCommand<Music>(PlayAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        public static IRelayCommand<Music> UpdateFavouriteCommand { get; } = new RelayCommand<Music>(UpdateFavourite);
        public static IRelayCommand<Music> AddToPlayListCommand { get; } = new RelayCommand<Music>(AddToCurrentPlayList);
        public static IRelayCommand<Music> AddToFavouriteCommand { get; } = new RelayCommand<Music>(AddToFavourite);
        /// <summary>按歌单关系身份播放，区分同曲多个位置。</summary>
        public static IAsyncRelayCommand<PlayListMusicItem> PlayPlaylistEntryCommand { get; } = new AsyncRelayCommand<PlayListMusicItem>(
            item => item is null ? Task.CompletedTask : PlayPlaylistEntryAsync(item), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        // 本地曲目的资源管理器定位入口；远程曲目由行按钮 IsEnabled=CanEditFile 与右键菜单置灰拦截。
        public static IRelayCommand<Music> OpenSourceCommand { get; } = new RelayCommand<Music>(OpenSource);

        public static IAsyncRelayCommand<MusicDetailsRequest> DetailsPlayCommand { get; } = new AsyncRelayCommand<MusicDetailsRequest>(
            request => request is null ? Task.CompletedTask : PlayFromPageAsync(request.Music, request.PlaybackPage, request.QueueEntryId, request.ExactVersion, request.PlaylistEntryId),
            _ => App.Services.GetRequiredService<AppViewModel>().CanStartPlayback,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);

        private static Task PlayAsync(Music? music)
            => music is null ? Task.CompletedTask : PlayFromPageAsync(music, AppData.CurrentPage);

        private static async Task PlayFromPageAsync(Music music, System.Type? page, long entryId = 0, bool exactVersion = false, int playlistEntryId = 0)
        {
            // 手动播放由 PlaybackCoordinator 重新探活，不能被缓存的离线状态拦截。
            if (music is null) return;
            var app = App.Services.GetRequiredService<AppViewModel>();
            if (page == typeof(PlayListPage))
            {
                foreach (var item in app.PlayListSongs)
                    if (playlistEntryId > 0 ? item.EntryId == playlistEntryId : item.Music.Id == music.Id)
                    {
                        await PlayPlaylistEntryAsync(item, music, exactVersion);
                        return;
                    }
                return;
            }
            if (page == typeof(FavouritePlayListPage))
                app.SetBrowseQueue(new(app.FavoriteSongs));
            else if (page == typeof(SongListPage))
                app.SetBrowseQueue(new(app.ListSongs));
            else if (page == typeof(AlbumPage))
                app.SetBrowseQueue(new(app.AlbumSongs));
            else if (page == typeof(ArtistPage))
                app.SetBrowseQueue(new(app.ArtistSongs));
            else if (page == typeof(FolderBrowsePage))
                app.SetBrowseQueue(new(app.FolderSongs), exactFiles: true, sourceScope: music.SourceId);

            await App.Services.GetRequiredService<PlaybackCoordinator>().PlayAsync(music, entryId,
                exactVersion: exactVersion || page == typeof(FolderBrowsePage));
        }

        /// <summary>从当前歌单建立队列并选择对应出现项。</summary>
        public static async Task PlayPlaylistEntryAsync(PlayListMusicItem selected, Music? exactMusic = null, bool exact = false,
            System.Collections.Generic.IReadOnlyList<PlayListMusicItem>? playlistItems = null)
        {
            var app = App.Services.GetRequiredService<AppViewModel>();
            if (!app.CanStartPlayback) return;
            var items = playlistItems ?? app.PlayListSongs;
            int targetIndex = -1;
            for (int i = 0; i < items.Count; i++)
                if (ReferenceEquals(items[i], selected) || selected.EntryId > 0 && selected.EntryId == items[i].EntryId) { targetIndex = i; break; }
            if (targetIndex < 0) return;
            AddPlaylistItems(app, items, replace: true);
            long entryId = app.State.Queue.SequentialEntryIdAt(targetIndex);
            await App.Services.GetRequiredService<PlaybackCoordinator>().PlayAsync(exactMusic ?? selected.Music, entryId,
                exactVersion: exact || selected.RequestedMusicId is not null);
        }

        internal static void AddPlaylistItems(AppViewModel app, System.Collections.Generic.IReadOnlyList<PlayListMusicItem> items, bool replace = false)
        {
            var music = new Music[items.Count];
            var entries = new int[items.Count];
            var requested = new int[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                music[i] = items[i].Music;
                entries[i] = items[i].EntryId;
                requested[i] = items[i].RequestedMusicId ?? 0;
            }
            int scope = app.State.Browse.SourceFilterId;
            if (replace) app.State.Queue.Replace(new(music), scope, playlistEntries: entries, requestedVersions: requested);
            else app.State.Queue.InsertNext(app.CurrentPlayingMusic, music, scope, playlistEntries: entries, requestedVersions: requested);
        }

        private static void UpdateFavourite(Music? music)
        {
            if (music is null) return;
            music.IsFavorite = !music.IsFavorite;
            var app = App.Services.GetRequiredService<AppViewModel>();
            if (music.IsFavorite)
            {
                music.Order = ComputeNextFavoriteOrder(app);
            }
            else
            {
                music.Order = 0;
            }
            PublishFavoriteProjection(app, music);
            PersistFavorite(music);
        }

        private static void AddToFavourite(Music? music)
        {
            if (music is null || music.IsFavorite) return;
            music.IsFavorite = true;
            var app = App.Services.GetRequiredService<AppViewModel>();
            music.Order = ComputeNextFavoriteOrder(app);
            PublishFavoriteProjection(app, music);
            PersistFavorite(music);
        }

        private static void PersistFavorite(Music music)
        {
            var database = App.Services.GetRequiredService<MusicDatabaseService>();
            _ = App.Services.GetRequiredService<ApplicationTasks>().RunCommittedAsync(async () =>
            {
                await database.AddToFavourite(music);
            });
        }

        private static void PublishFavoriteProjection(AppViewModel app, Music music)
        {
            if (music.SongId > 0)
            {
                foreach (var version in app.FindVersions(music.SongId))
                {
                    version.IsFavorite = music.IsFavorite;
                    version.Order = music.Order;
                }
                if (app.CurrentPlayingMusic is { } current && current.SongId == music.SongId)
                { current.IsFavorite = music.IsFavorite; current.Order = music.Order; }
            }
            app.NotifyIdIndexChanged();
            app.NotifySharedFavoriteChanged();
            app.RefreshAllViews();
        }

        private static void AddToCurrentPlayList(Music? music)
        {
            if (music is null) return;
            App.Services.GetRequiredService<AppViewModel>().AddMusicToCurrentPlayList(music, AppData.CurrentPage == typeof(FolderBrowsePage));
        }

        private static void OpenSource(Music? music)
        {
            if (music is null) return;
            // 失败细节（文件缺失或资源管理器启动失败）由 ToolUtils 记录，这里只做界面提示。
            if (!ToolUtils.OpenFileInExplorer(music.Path))
                ShowShellError("OpenLocationFileMissing");
        }

        private static void ShowShellError(string messageKey)
        {
            var shell = App.Services.GetRequiredService<AppViewModel>().State.Shell;
            shell.InfoBarTitle = ToolUtils.GetString("Error");
            shell.InfoBarMessage = ToolUtils.GetString(messageKey);
            shell.InfoBarIsOpen = true;
        }

        private static int ComputeNextFavoriteOrder(AppViewModel app)
        {
            var span = CollectionsMarshal.AsSpan(app.SongsSource);
            int max = 0;
            for (int i = 0; i < span.Length; i++)
            {
                var m = span[i];
                if (m.IsFavorite && m.Order > max) max = m.Order;
            }
            return max + 1;
        }

        public static void OnLyricsOffsetChanged(Music music, int value)
        {
            if (!App.Services.GetRequiredService<AppViewModel>().IsInitialized)
                return;

            // SQLite 在线程池物化对象时也调用 setter，读库不能发布偏移或把半初始化对象写回。
            var window = App.MainWindow;
            if (window is null || !window.DispatcherQueue.HasThreadAccess) return;
            var app = App.Services.GetRequiredService<AppViewModel>();
            if (!app.CanStartPlayback) return;
            if (app.CurrentPlayingMusic?.Id == music.Id)
                OffsetMsBus.Publish(value);
            _ = App.Services.GetRequiredService<MusicDatabaseService>().UpdateLyricsOffsetAsync(music.Id, value);
        }

        public static async Task RemoveMusicAsync(Music music)
        {
            await App.Services.GetRequiredService<MusicDatabaseService>().RemoveMusic(music.Id);
            var app = App.Services.GetRequiredService<AppViewModel>();
            app.RefreshAllViews();
        }
    }
}
