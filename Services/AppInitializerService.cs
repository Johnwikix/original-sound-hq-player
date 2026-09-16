using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
namespace WinUIMusicPlayer.Services;

// Host 适配层；所有启动顺序集中在 StartupCoordinator。
public sealed class AppInitializerService(StartupCoordinator startup) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => startup.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
