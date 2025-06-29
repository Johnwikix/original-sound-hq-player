using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;

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
