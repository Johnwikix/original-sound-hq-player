using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public class AppInitializerService : IHostedService
    {
        private readonly AppViewModel AppViewModel;
        private readonly MusicDatabaseService MusicDatabaseService;
        public AppInitializerService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            AppViewModel = appViewModel;
            MusicDatabaseService = musicDatabaseService;
        }
        // 应用启动时执行初始化
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await MusicDatabaseService.Initialize();
            var tasks = new Task[] {                 
                 MusicDatabaseService.GetEqualizerSettingsAsync(),
                 MusicDatabaseService.GetSettingsAsync()                 
            };
            await Task.WhenAll(tasks);
            App.MainWindow = App.Services.GetRequiredService<MainWindow>();
            App.MainWindow.Activate();            
            var longOpsTask = Task.Run(async () =>
            {
                await InitialFileScan.InitialScan();
                await MusicDatabaseService.LoadMusicList();
                await MusicDatabaseService.GetPlayStateAsync();                
            }, cancellationToken);            
            await Task.Delay(500, cancellationToken);
            App.Services.GetRequiredService<IpcService>().Initializing();
            await Task.Delay(500, cancellationToken);
            await Task.WhenAll(longOpsTask);
            ToolUtils.CleanupStaleCacheFiles();
            await App.Services.GetRequiredService<MusicBrowseViewModel>().LoadPlayStateToMusicBrowsePage();
            await App.Services.GetRequiredService<IpcService>().InitializeMusic(App.Services.GetRequiredService<AppViewModel>().CurrentPlayingMusic);           
            App.MainWindow.ShowMainPage();
            App.Services.GetRequiredService<PlayingDetailPage>().PreLoadImgData();
            App.Services.GetRequiredService<AppViewModel>().IsInitialized = true;            
        }

        // 应用关闭时执行清理
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
