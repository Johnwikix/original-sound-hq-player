using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using OriginalSound.Plugin;

if (args.Length != 2) return 2;
// Reserve stdout for framed RPC; even a plugin using Console.WriteLine cannot corrupt it.
using var output = Console.OpenStandardOutput();
using var input = Console.OpenStandardInput();
Console.SetOut(Console.Error);
try
{
    string manifestPath = Path.GetFullPath(args[0]);
    var manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath), PluginJson.Default.PluginManifest)
        ?? throw new InvalidDataException();
    if (manifest.ApiVersion != 1) throw new InvalidDataException("Unsupported plugin API.");
    string root = Path.GetDirectoryName(manifestPath)! + Path.DirectorySeparatorChar;
    string entry = Path.GetFullPath(Path.Combine(root, manifest.EntryAssembly));
    if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid plugin entry.");
    var context = new PluginContext(entry);
    var type = context.LoadFromAssemblyPath(entry).GetType(manifest.EntryType, true)!;
    await using var plugin = (IPlayerPlugin)Activator.CreateInstance(type)!;
    Directory.CreateDirectory(args[1]);
    await plugin.InitializeAsync(args[1], CancellationToken.None);
    await PluginWire.WriteAsync(output, new PluginReply { Id = 0, Result = PluginResult.Found }, PluginJson.Default.PluginReply, default);
    while (true)
    {
        PluginRequest request;
        try { request = await PluginWire.ReadAsync(input, PluginJson.Default.PluginRequest, default); }
        catch (EndOfStreamException) { break; }
        if (request.Method == "shutdown") break;
        PluginReply reply;
        try { reply = await plugin.HandleAsync(request, default); }
        catch { reply = new PluginReply { Result = PluginResult.NetworkError, Error = "Plugin operation failed." }; }
        await PluginWire.WriteAsync(output, reply with { Id = request.Id }, PluginJson.Default.PluginReply, default);
    }
    return 0;
}
catch { return 1; }

sealed class PluginContext(string entry) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(entry);
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IPlayerPlugin).Assembly.GetName().Name) return typeof(IPlayerPlugin).Assembly;
        string? path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
