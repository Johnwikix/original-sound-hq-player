using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
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
                 MusicDatabaseService.GetPlayStateAsync(),
                 MusicDatabaseService.GetSettingsAsync()
            };
            await Task.WhenAll(tasks);
            AppViewModel.IsInitialized = true;
        }

        // 应用关闭时执行清理
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
