using Microsoft.Extensions.DependencyInjection;
using WinUIMusicPlayer.Services.AudioAnalysis;
namespace WinUIMusicPlayer
{
    public static class App { public static IServiceProvider Services { get; set; } = null!; }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        private static readonly Dictionary<string,string> Values=System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory,"resources.resw")).Root!.Elements("data").ToDictionary(x=>(string)x.Attribute("name")!,x=>(string)x.Element("value")!);
        public static string GetString(string key)=>Values.TryGetValue(key,out var value)?value:key;
    }
}
internal sealed class Source : IAudioAnalysisSettingsSource
{
    public AnalysisProgress Progress { get; private set; } = new();
    public event Action? Changed;
    public async Task SetEnabledAsync(bool enabled) { await Task.Delay(40); Publish(Progress with {Enabled=enabled,Running=false,Phase=enabled?"Idle":"Disabled",CurrentTitle=""}); }
    public void Publish(AnalysisProgress progress) { Progress=progress;Changed?.Invoke(); }
}
