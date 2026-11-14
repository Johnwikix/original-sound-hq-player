using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

/// <summary>编译真实 AlbumDetailsViewModel，覆盖草稿隔离、只写变更字段、懒读门槛、部分失败与关闭排空。</summary>
internal static class AlbumDetailsChecks
{
    /// <summary>检查专辑编辑会话的核心保存语义，并可靠收尾测试任务。</summary>
    internal static async Task RunAsync(MusicDatabaseService database, AppViewModel app, Action<bool, string> check)
    {
        string album = "Album Fixture " + Guid.NewGuid().ToString("N")[..6];
        var first = NewTrack(990111, album, "Artist A", 2019);
        var second = NewTrack(990112, album, "Artist B", 2019);
        var third = NewTrack(990113, album, "Artist A; Artist C", 2019);
        var songs = app.State.Library.Songs;
        songs.AddRange([first, second, third]);
        ToolUtils.PrepareArtwork(first.Id);
        ToolUtils.PrepareArtwork(990114);
        var replacedCover = await ToolUtils.GetRawImage(new Music { Id = 990114 });
        var host = new AlbumHost(() => Task.FromResult(replacedCover));
        var sessions = new EditorSessions();
        var remote = NewTrack(990115, "Remote album fixture", "Remote Artist", 2020);
        remote.SourceId = 2;
        songs.Add(remote);
        try
        {
            await CheckSessionAsync();
            await CheckCloseDrainAsync();
            var remoteSession = new AlbumDetailsViewModel(remote, app, database, sessions, host, NullLogger<AlbumDetailsViewModel>.Instance);
            check(!remoteSession.CanEditTags && !remoteSession.CanSaveFile && !remoteSession.CanSaveDatabase && !remoteSession.FindCoverCommand.CanExecute(null),
                "remote albums stay read-only with both save entries and online cover disabled");
            await remoteSession.StopAsync();
        }
        finally
        {
            songs.RemoveAll(m => m.Id is 990111 or 990112 or 990113 or 990115);
        }
        return;

        async Task CheckSessionAsync()
        {
            var vm = new AlbumDetailsViewModel(first, app, database, sessions, host, NullLogger<AlbumDetailsViewModel>.Instance);
            int closes = 0;
            vm.CloseRequested += (_, _) => closes++;
            vm.Start();
            for (int i = 0; vm.IsLoading && i < 500; i++) await Task.Delay(20);
            check(!vm.IsLoading && vm.Error.Length == 0 && vm.CoverPreview is not null,
                "album session finishes loading and publishes the representative cover");

            // 曲目装配：三首全部列出；两位以上艺术家在概要行点名，艺术家回退行用原始作者顺序连接。
            check(vm.Tracks.Count == 3 && vm.Summary.Contains(ToolUtils.GetString("NumberOfArtists"))
                && vm.HeroArtist == "Artist A · Artist B · Artist A; Artist C",
                "album session lists every track and flags the multi-artist album");

            // 无改动保存：直接成功，不产生任何写入。
            int writes = database.AlbumWriteStarted;
            await vm.SaveDatabaseCommand.ExecuteAsync(null);
            check(closes == 1 && database.AlbumWriteStarted == writes,
                "saving without changes succeeds without touching any file");

            // 草稿隔离：编辑只落在草稿，共享曲目保持原值。
            string renamed = album + " renamed";
            vm.Draft.Album = renamed;
            check(first.Album == album && vm.HeroArtist.StartsWith("Artist A"),
                "draft edits never touch the shared tracks before saving");

            // 年份变更传播到全部曲目（含内存对象）；同批未保存的改名一并提交。
            vm.Draft.Year = 2021;
            await vm.SaveDatabaseCommand.ExecuteAsync(null);
            check(closes == 2 && first.Year == 2021 && second.Year == 2021 && third.Year == 2021
                && first.Album == renamed && third.Album == renamed,
                "changed year applies to every album track together with pending draft edits");

            // 专辑艺术家遵循懒读门槛：只有已读取可选标签的曲目接收新值。
            vm.Draft.AlbumArtist = "Various Fixture";
            await vm.SaveDatabaseCommand.ExecuteAsync(null);
            check(closes == 3 && first.AlbumArtist == "Various Fixture" && second.AlbumArtist.Length == 0 && third.AlbumArtist.Length == 0,
                "album artist reaches tracks with loaded optional tags only");
            second.AdditionalMetadataRead = true;
            vm.Draft.AlbumArtist = "Various Fixture 2";
            await vm.SaveDatabaseCommand.ExecuteAsync(null);
            check(closes == 4 && first.AlbumArtist == "Various Fixture 2" && second.AlbumArtist == "Various Fixture 2",
                "re-saving applies the album artist to newly loaded tracks too");

            // 部分失败：占用曲目计数不中断其余写入、不关窗、错误可见。
            database.AlbumFailPaths.Add(third.Path);
            vm.Draft.Album = album + " final";
            await vm.SaveFileCommand.ExecuteAsync(null);
            check(closes == 4 && vm.Error.Length > 0 && database.AlbumWriteStarted == writes + 3
                && first.Album.EndsWith("final") && second.Album.EndsWith("final") && third.Album == renamed,
                "one locked file cannot block the others and keeps the window open with the error");

            // 清除占用后重试：剩余曲目补齐并正常关窗。
            database.AlbumFailPaths.Clear();
            await vm.SaveFileCommand.ExecuteAsync(null);
            check(closes == 5 && vm.Error.Length == 0 && third.Album.EndsWith("final"),
                "retry after the lock clears finishes the remaining tracks and closes");

            // 更换封面后，封面是唯一待写变更，批次照常执行。
            var previousPreview = vm.CoverPreview;
            await vm.ChooseCoverCommand.ExecuteAsync(null);
            check(vm.Error.Length == 0 && vm.CoverPreview is not null && !ReferenceEquals(vm.CoverPreview, previousPreview),
                "choosing a local cover replaces only the staged preview");
            await vm.SaveFileCommand.ExecuteAsync(null);
            check(closes == 6 && database.AlbumWriteStarted == writes + 9,
                "a replaced cover alone triggers the write batch for every track");
            await vm.StopAsync();
        }

        async Task CheckCloseDrainAsync()
        {
            var vm = new AlbumDetailsViewModel(first, app, database, sessions, host, NullLogger<AlbumDetailsViewModel>.Instance);
            vm.Start();
            for (int i = 0; vm.IsLoading && i < 500; i++) await Task.Delay(20);
            vm.Draft.Genre = "Fixture Genre";
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            database.AlbumWriteGate = hold.Task;
            var saving = vm.SaveFileCommand.ExecuteAsync(null);
            vm.BeginClose();
            var stopping = vm.StopAsync();
            check(!stopping.IsCompleted && !saving.IsCompleted,
                "closing waits for an in-flight album write instead of pretending it stopped");
            hold.SetResult();
            await Task.WhenAll(stopping.WaitAsync(TimeSpan.FromSeconds(5)), saving.WaitAsync(TimeSpan.FromSeconds(5)));
            check(first.Genre == "Fixture Genre" && second.Genre == "Fixture Genre" && third.Genre == "Fixture Genre",
                "the started write batch finishes and publishes even after close began");
            database.AlbumWriteGate = Task.CompletedTask;
        }
    }

    private static Music NewTrack(int id, string album, string author, int year) => new()
    {
        Id = id, Title = "Track " + id, Album = album, Author = author, Year = year,
        Path = $"album-fixture-{id}.flac", SourceId = 0
    };

    /// <summary>隔离专辑会话的封面来源；本地替换返回固定字节供更换封面对比。</summary>
    private sealed class AlbumHost(Func<Task<byte[]?>> choose) : IMusicDetailsEditorHost
    {
        public Task<byte[]?> ReadCoverAsync(Music music, CancellationToken token) => ToolUtils.GetRawImage(music, true, token);
        public Task<byte[]?> FindCoverAsync(Music music, CancellationToken token) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> ChooseCoverAsync(CancellationToken token) => choose();
        public Task ExportCoverAsync(byte[] cover, CancellationToken token) => Task.CompletedTask;
        public void OpenLocation(Music music) { }
        public void MetadataChanged() { }
        public void DatabaseSaved(Music music) { }
        public void NotifyDatabaseSaveFailed(Music music, Exception exception) { }
        public void NotifyWriteQueued(Music music) { }
    }
}
