using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class AppInitializerService : IHostedService
    {
        private readonly AppObservableObj _appObservableObj;
        private readonly MusicDatabaseService MusicDatabaseService;
        public AppInitializerService(AppObservableObj appObservableObj, MusicDatabaseService musicDatabaseService)
        {
            this._appObservableObj = appObservableObj;
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
            _appObservableObj.IsInitialized = true;
        }

        // 应用关闭时执行清理
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
