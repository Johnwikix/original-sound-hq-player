using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services
{
    public class DatabaseInitializerService : IHostedService
    {

        // 应用启动时执行初始化
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await MusicDatabaseService.Initialize();
            var tasks = new Task[] {
                 MusicDatabaseService.GetPlayStateAsync(),
                 MusicDatabaseService.GetSettingsAsync()
            };
            await Task.WhenAll(tasks);
        }

        // 应用关闭时执行清理
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
