using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

var directory = AppContext.BaseDirectory;
void Log(string value) => File.AppendAllText(Path.Combine(directory, "PublishedProbe.log"), value + "\n");
bool live = args.Length == 1 && args[0] == "--live";
string phase = "runtime";
try
{
    foreach (string name in new[] { "coreclr.dll", "hostpolicy.dll" })
        RequireLocal(name, NativeModulePath(name));
    foreach (Assembly assembly in new[] { typeof(object).Assembly, typeof(HttpClient).Assembly, typeof(JsonDocument).Assembly })
        RequireLocal(assembly.GetName().Name + ".dll", assembly.Location);
    if (!AppContext.TryGetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", out bool reflection) || reflection)
        throw new InvalidOperationException("Expected the package's trimmed runtime configuration.");

    Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "OriginalSound HIFI Player.dll"));
    Log("Product assembly: " + app.Location);
    Type mediaType = app.GetType("WinUIMusicPlayer.Services.RemoteSources.RemoteMediaHttp", throwOnError: true)!;
    Type providerType = app.GetType("WinUIMusicPlayer.Services.RemoteSources.JellyfinProvider", throwOnError: true)!;
    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    using var transport = (IDisposable)mediaType.GetConstructors(flags).Single().Invoke(new object?[] { live ? null : new FixtureHandler() });
    var provider = providerType.GetConstructors(flags).Single().Invoke(new object?[] { transport });
    string address = live ? ReadEnvironment("REMOTE_CATALOG_URL") : "http://127.0.0.1:33333/";
    string user = live ? ReadEnvironment("REMOTE_CATALOG_USER") : "fixture-user";
    string password = live ? ReadEnvironment("REMOTE_CATALOG_PASSWORD") : "fixture-password";

    phase = "AuthenticateAsync";
    var auth = (Task)providerType.GetMethod(phase, flags)!.Invoke(provider, new object?[] {
        new Uri(address), user, password, CancellationToken.None, null })!;
    auth.GetAwaiter().GetResult();
    var session = auth.GetType().GetProperty("Result")!.GetValue(auth);
    Log("PASS Jellyfin sign-in.");
    phase = "ProbeAsync";
    ((Task)providerType.GetMethod(phase, flags)!.Invoke(provider, new[] { session, CancellationToken.None })!).GetAwaiter().GetResult();
    Log("PASS Jellyfin authenticated catalog probe.");
    return 0;
}
catch (Exception ex)
{
    if (ex is TargetInvocationException invocation && invocation.InnerException is not null) ex = invocation.InnerException;
    var type = ex.GetType();
    Log($"FAIL {phase}: {type.Name}; code={type.GetProperty("Code")?.GetValue(ex)}; HTTP={type.GetProperty("Status")?.GetValue(ex)}");
    // Diagnostic fixture values are synthetic. Live exceptions can contain private endpoint information.
    if (!live) Log(ex.ToString());
    return 1;
}

void RequireLocal(string name, string location)
{
    if (!string.Equals(Path.Combine(directory, name), location, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unexpected runtime module path for " + name);
    Log("Package runtime: " + location);
}

static string ReadEnvironment(string name) => Environment.GetEnvironmentVariable(name) ??
    throw new InvalidOperationException("Missing test environment variable " + name);

static string NativeModulePath(string name)
{
    var module = Native.GetModuleHandle(name);
    if (module == IntPtr.Zero) return "";
    var path = new StringBuilder(2048);
    return Native.GetModuleFileName(module, path, 2048) == 0 ? "" : path.ToString();
}

sealed class FixtureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path == "/Users/AuthenticateByName" && request.Method == HttpMethod.Post)
        {
            using var output = new MemoryStream();
            request.Content!.CopyToAsync(output, null, cancellationToken).GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(output.ToArray());
            if (request.Content.Headers.ContentType is not { MediaType: "application/json", CharSet: "utf-8" }
                || document.RootElement.GetProperty("Username").GetString() != "fixture-user"
                || document.RootElement.GetProperty("Pw").GetString() != "fixture-password")
                throw new InvalidOperationException("Unexpected Jellyfin login body.");
            return Reply("{\"User\":{\"Id\":\"7ccd2c55aad1492aac251be1379a1d13\"},\"AccessToken\":\"fixture-token\"}");
        }
        if (path != "/Items" || request.Method != HttpMethod.Get)
            throw new InvalidOperationException("Unexpected Jellyfin request.");
        return Reply("{\"Items\":[],\"TotalRecordCount\":0}");
    }

    private static Task<HttpResponseMessage> Reply(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    });
}

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern uint GetModuleFileName(IntPtr module, StringBuilder result, uint capacity);
}
