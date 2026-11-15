using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace WinUIMusicPlayer;

internal static class App
{
#if USB_UI_REGRESSION
    public static Microsoft.UI.Xaml.Window MainWindow { get; set; } = null!;
#endif
    public static IServiceProvider Services { get; } = new ServiceCollection()
        .AddSingleton<Services.MusicDatabaseService>().BuildServiceProvider();
    public static ILogger<T> GetLogger<T>() => new ProbeLogger<T>();

    private sealed class ProbeLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) Console.Error.WriteLine($"{level}: {formatter(state, error)} {error}");
        }
    }
}
