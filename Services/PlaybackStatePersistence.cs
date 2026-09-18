using Microsoft.UI.Windowing;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
namespace WinUIMusicPlayer.Services;
public sealed class PlaybackStatePersistence(MusicDatabaseService db, AppViewModel appvm, ILogger<PlaybackStatePersistence> _logger)
{
    public async Task SaveAsync(MainWindow MainWindow)
    {
        try
        {
            if (db == null || appvm == null || MainWindow == null) return;

            // 退出时 Presenter 的保存策略:
            //   OverlappedPresenter (Kind=Overlapped):
            //     Restored    -> 用当前 bounds 覆盖, IsMaximized=false
            //     Maximized   -> 沿用旧 bounds 不动, IsMaximized=true
            //     Minimized   -> 沿用旧 bounds 不动, IsMaximized=false
            //   FullScreenPresenter (Kind=FullScreen):
            //     全屏期间 AppWindow.Position/Size 是显示器尺寸, 没有意义 — 不读, 沿用旧 bounds 不动, IsMaximized=false
            //   presenter 为 null 或其他未知 Kind:
            //     完全沿用旧存档, IsMaximized 也沿用旧值 (无法判断时不做任何修改)
            //
            // 关键: 任何情况下都不能写入 0,0,0,0 覆盖已有正确 bounds; 所有非 Restored 路径都从 db.CurrentPlayState 继承。
            // 其他字段(PlayMode/Volume/LastPlayedMusicId/SortOrder)无论 presenter 类型/状态都保存。
            var appWindow = MainWindow.AppWindow;
            var presenter = appWindow?.Presenter;
            var existing = db.CurrentPlayState;

            bool hasWindowBounds;
            bool isMaximized;
            int x, y, w, h;

            if (presenter is OverlappedPresenter op)
            {
                var state = op.State;

                if (state == OverlappedPresenterState.Restored)
                {
                    var pos = appWindow!.Position;
                    var size = appWindow.Size;
                    x = pos.X;
                    y = pos.Y;
                    w = size.Width;
                    h = size.Height;
                    hasWindowBounds = true;
                    isMaximized = false;
                }
                else if (state == OverlappedPresenterState.Maximized)
                {
                    // 关键修复: 最大化状态下保存本次会话的还原矩形 (含次屏坐标),
                    // 而不是磁盘旧值, 否则下次启动会错误地恢复到旧显示器.
                    var tracked = MainWindow.TrackedBounds;
                    if (MainWindow.HasTrackedBounds
                        && tracked.Width > 0 && tracked.Height > 0)
                    {
                        x = tracked.X;
                        y = tracked.Y;
                        w = tracked.Width;
                        h = tracked.Height;
                        hasWindowBounds = true;
                    }
                    else
                    {
                        hasWindowBounds = existing?.HasWindowBounds ?? false;
                        x = existing?.WindowX ?? 0;
                        y = existing?.WindowY ?? 0;
                        w = existing?.WindowWidth ?? 0;
                        h = existing?.WindowHeight ?? 0;
                    }
                    isMaximized = true;
                }
                else
                {
                    // Minimized
                    hasWindowBounds = existing?.HasWindowBounds ?? false;
                    x = existing?.WindowX ?? 0;
                    y = existing?.WindowY ?? 0;
                    w = existing?.WindowWidth ?? 0;
                    h = existing?.WindowHeight ?? 0;
                    isMaximized = false;
                }
            }
            else if (presenter?.Kind == AppWindowPresenterKind.FullScreen)
            {
                // FullScreenPresenter 与 OverlappedPresenter 是兄弟类(共享 AppWindowPresenter 基类),
                // 在此状态下 AppWindow.Position/Size 返回整个显示器尺寸, 不能用作 bounds.
                hasWindowBounds = existing?.HasWindowBounds ?? false;
                x = existing?.WindowX ?? 0;
                y = existing?.WindowY ?? 0;
                w = existing?.WindowWidth ?? 0;
                h = existing?.WindowHeight ?? 0;
                isMaximized = false;
            }
            else
            {
                // presenter 为 null 或未知 Kind: 完全沿用旧存档
                hasWindowBounds = existing?.HasWindowBounds ?? false;
                x = existing?.WindowX ?? 0;
                y = existing?.WindowY ?? 0;
                w = existing?.WindowWidth ?? 0;
                h = existing?.WindowHeight ?? 0;
                isMaximized = existing?.IsMaximized ?? false;
            }

            var playState = new SavePlayState
            {
                PlayMode = appvm.CurrentPlayMode,
                // 未入库的一次性外部曲目（Id=0）不覆盖当前曲存档，避免下次启动恢复不出任何曲目。
                LastPlayedMusicId = appvm.CurrentPlayingMusic is { Id: > 0 } current ? current.Id : existing?.LastPlayedMusicId,
                Volume = appvm.Volume,
                SortOrder = appvm.SelectedSortOption?.Tag?.ToString() ?? "DefaultOrder",
                HasWindowBounds = hasWindowBounds,
                WindowX = x,
                WindowY = y,
                WindowWidth = w,
                WindowHeight = h,
                IsMaximized = isMaximized
            };
            await db.SavePlayStateAsync(playState, appvm.SequentialPlayingList);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "保存播放状态失败: {Message}", ex.Message);
        }
    }
}
