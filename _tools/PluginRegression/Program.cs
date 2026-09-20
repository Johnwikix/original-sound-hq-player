using System.Diagnostics;
using System.Text.Json;
using OriginalSound.Plugin;
using OriginalSound.Plugin.Runtime;

if (args.Length is < 1 or > 2) throw new ArgumentException("Pass the published PluginHost.exe path.");
string directory = Path.Combine(AppContext.BaseDirectory, "fixture");
Directory.CreateDirectory(directory);
string manifest = Path.Combine(AppContext.BaseDirectory, "plugin.json");
await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new PluginManifest
{
    Id = "test.probe", Name = "Probe", EntryAssembly = "PluginRegression.dll", EntryType = typeof(ProbePlugin).FullName!
}, PluginJson.Default.PluginManifest));
await using var process = new PluginProcess(Path.GetFullPath(args[0]), manifest, directory);
await process.InvokeAsync(null, default);
Check((await process.InvokeAsync(new() { Method = "echo" }, default)).Result == PluginResult.Found, "real worker handshake and dispatch");
using (var cancel = new CancellationTokenSource(250))
{
    var timer = Stopwatch.StartNew();
    try { await process.InvokeAsync(new() { Method = "hang" }, cancel.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
    Check(timer.Elapsed < TimeSpan.FromSeconds(4), "non-cooperative plugin is stopped on cancellation");
}
Check((await process.InvokeAsync(new() { Method = "echo" }, default)).Result == PluginResult.Found, "worker restarts after cancellation");
try { await process.InvokeAsync(new() { Method = "crash" }, default); throw new Exception("Crash was hidden"); }
catch (EndOfStreamException) { }
Check((await process.InvokeAsync(new() { Method = "echo" }, default)).Result == PluginResult.Found, "worker restarts after crash");
using (var stream = new MemoryStream([255, 255, 255, 127]))
{
    try { await PluginWire.ReadAsync(stream, PluginJson.Default.PluginReply, default); throw new Exception("Unbounded frame accepted"); }
    catch (InvalidDataException) { Console.WriteLine("PASS oversized response rejected before allocation"); }
}
await process.DisposeAsync();
try { await process.InvokeAsync(new() { Method = "echo" }, default); throw new Exception("Disposed worker restarted"); }
catch (OperationCanceledException) { Console.WriteLine("PASS disposed worker cannot restart"); }
if (args.Length == 2)
{
    string source = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
    string deploymentRoot = Path.Combine(directory, "deployment-" + Guid.NewGuid().ToString("N"));
    string installed = Path.Combine(deploymentRoot, "Plugins", "originalsound.lyrics-search");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try { BundledPluginInstaller.InstallIfMissing(source, installed, canceled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(!Directory.Exists(installed), "canceled installation leaves no installed plugin");
    }
    BundledPluginInstaller.InstallIfMissing(source, installed, default);
    Check(Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).All(file =>
        File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(installed, Path.GetRelativePath(source, file))))),
        "bundled plugin installs with all dependencies intact");
    string marker = Path.Combine(installed, "user-version.txt");
    File.WriteAllText(marker, "keep");
    string installedManifest = Path.Combine(installed, "plugin.json");
    File.AppendAllText(installedManifest, "\n ");
    string retainedManifest = File.ReadAllText(installedManifest);
    BundledPluginInstaller.InstallIfMissing(source, installed, default);
    Check(File.ReadAllText(marker) == "keep" && File.ReadAllText(installedManifest) == retainedManifest,
        "repeated startup preserves installed version and user files");
    await using var lyrics = new PluginProcess(Path.GetFullPath(args[0]), installedManifest, Path.Combine(directory, "lyrics"));
    var reply = await lyrics.InvokeAsync(new() { Method = "probe", Track = new("", "", "", "", null, "en") }, default);
    Check(reply.Result == PluginResult.Unsupported, "deployed bundled lyrics plugin loads through the real host");
}
static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }

public sealed class ProbePlugin : IPlayerPlugin
{
    public ValueTask InitializeAsync(string dataDirectory, CancellationToken ct) => ValueTask.CompletedTask;
    public async ValueTask<PluginReply> HandleAsync(PluginRequest request, CancellationToken ct)
    {
        Console.WriteLine("Plugin diagnostic output must not corrupt RPC.");
        if (request.Method == "hang") await Task.Delay(Timeout.Infinite, CancellationToken.None);
        if (request.Method == "crash") Environment.Exit(17);
        return new() { Result = PluginResult.Found };
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
