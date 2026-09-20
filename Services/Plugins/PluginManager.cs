using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using OriginalSound.Plugin;
using OriginalSound.Plugin.Runtime;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services.Plugins;

public sealed partial class PluginItem : ObservableObject
{
    public required PluginManifest Manifest { get; init; }
    public required string ManifestPath { get; init; }
    public string Name => Manifest.Name;
    public string Details => $"{Manifest.Version} · {Manifest.Author}";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActionLabel))] private bool enabled;
    [ObservableProperty] private bool active;
    [ObservableProperty] private string status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChange))] private bool busy;
    public string ActionLabel => ToolUtils.GetString(Enabled ? "PluginDisable" : "PluginEnable");
    public bool CanChange => Valid && !Busy;
    public bool Valid { get; init; } = true;
}

public sealed record PluginRoute(string PluginId, string PageId, string Title);

/// <summary>Owns workers. Collection and route notifications are published only on the attached UI dispatcher.</summary>
public sealed class PluginManager
{
    private readonly SemaphoreSlim _changes = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, PluginProcess> _workers = new(StringComparer.Ordinal);
    private volatile (PluginItem Item, PluginProcess Worker)[] _active = [];
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
    private DispatcherQueue? _dispatcher;
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Plugins");
    private string DataRoot => Path.Combine(Path.GetDirectoryName(Root)!, "PluginData");
    public ObservableCollection<PluginItem> Items { get; } = [];
    public event Action? RoutesChanged;
    public bool HasLyrics => _active.Any(x => x.Item.Manifest.Capabilities.Contains("lyrics"));
    public PluginRoute[] Routes => _active.SelectMany(x => x.Item.Manifest.Pages.Select(p => new PluginRoute(x.Item.Manifest.Id, p.Id, p.Title))).ToArray();

    public async Task StartAsync(CancellationToken ct)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _changes.WaitAsync(linked.Token);
        try
        {
            var manifests = await Task.Run(() => Scan(linked.Token), linked.Token);
            foreach (var item in manifests) Items.Add(item);
            foreach (var item in Items.Where(x => x.Enabled && x.Valid))
            {
                linked.Token.ThrowIfCancellationRequested();
                await StartItemAsync(item, linked.Token);
            }
        }
        finally { _changes.Release(); }
    }

    private List<PluginItem> Scan(CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataRoot);
        try
        {
            BundledPluginInstaller.InstallIfMissing(
                Path.Combine(AppContext.BaseDirectory, "BundledPlugins", "originalsound.lyrics-search"),
                Path.Combine(Root, "originalsound.lyrics-search"), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Bundled plugin deployment failed: {ex.Message}");
        }
        string settings = Path.Combine(DataRoot, "enabled.json");
        if (File.Exists(settings))
        {
            try { foreach (var id in JsonSerializer.Deserialize(File.ReadAllText(settings), PluginJson.Default.StringArray) ?? []) _enabled.Add(id); }
            catch (JsonException) { /* Corrupt settings fail closed: no executable code is loaded. */ }
        }
        var result = new List<PluginItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in Directory.EnumerateDirectories(Root).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(folder, "plugin.json");
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException();
                var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), PluginJson.Default.PluginManifest) ?? throw new InvalidDataException();
                Validate(manifest, folder);
                if (!ids.Add(manifest.Id)) throw new InvalidDataException();
                result.Add(new() { Manifest = manifest, ManifestPath = path, Enabled = _enabled.Contains(manifest.Id), Status = ToolUtils.GetString("PluginDisabled") });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
            {
                result.Add(new() { Manifest = new() { Name = Path.GetFileName(folder) }, ManifestPath = path, Valid = false, Status = ToolUtils.GetString("PluginInvalid") });
            }
        }
        return result;
    }

    private static void Validate(PluginManifest manifest, string root)
    {
        if (manifest.ApiVersion != 1 || !Regex.IsMatch(manifest.Id, @"\A[a-z0-9][a-z0-9.-]{0,79}\z") ||
            string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.EntryType) ||
            manifest.Pages.Length > 8 || manifest.Capabilities.Length > 8) throw new InvalidDataException();
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string entry = Path.GetFullPath(Path.Combine(root, manifest.EntryAssembly));
        if (!entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry) || Path.GetExtension(entry) != ".dll")
            throw new InvalidDataException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in manifest.Pages)
            if (!Regex.IsMatch(page.Id, @"\A[a-z0-9][a-z0-9.-]{0,79}\z") || !ids.Add(page.Id) ||
                page.Kind != "lyrics-search" || !manifest.Capabilities.Contains("lyrics") || string.IsNullOrWhiteSpace(page.Title))
                throw new InvalidDataException();
    }

    public async Task SetEnabledAsync(PluginItem item, bool enabled)
    {
        await _changes.WaitAsync(_stop.Token);
        try
        {
            if (!item.Valid) return;
            item.Busy = true;
            var nextEnabled = new HashSet<string>(_enabled, StringComparer.Ordinal);
            if (enabled) nextEnabled.Add(item.Manifest.Id); else nextEnabled.Remove(item.Manifest.Id);
            string file = Path.Combine(DataRoot, "enabled.json");
            Directory.CreateDirectory(DataRoot);
            await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(nextEnabled.Order().ToArray(), PluginJson.Default.StringArray), _stop.Token);
            File.Move(file + ".tmp", file, true);
            _enabled.Clear();
            _enabled.UnionWith(nextEnabled);
            item.Enabled = enabled;
            if (_workers.Remove(item.Manifest.Id, out var previous))
            {
                item.Active = false;
                PublishActive();
                await previous.DisposeAsync();
            }
            if (enabled) await StartItemAsync(item, _stop.Token);
            else item.Status = ToolUtils.GetString("PluginDisabled");
        }
        finally { item.Busy = false; _changes.Release(); }
    }

    private async Task StartItemAsync(PluginItem item, CancellationToken ct)
    {
        item.Busy = true;
        item.Status = ToolUtils.GetString("PluginLoading");
        var worker = new PluginProcess(Path.Combine(AppContext.BaseDirectory, "PluginHost", "PluginHost.exe"), item.ManifestPath,
            Path.Combine(DataRoot, item.Manifest.Id));
        try
        {
            await worker.InvokeAsync(null, ct);
            ct.ThrowIfCancellationRequested();
            _workers.Add(item.Manifest.Id, worker);
            item.Active = true;
            item.Status = ToolUtils.GetString("PluginReady");
            PublishActive();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await worker.DisposeAsync();
            item.Status = ToolUtils.GetString("PluginLoadFailed");
        }
        catch { await worker.DisposeAsync(); throw; }
        finally { item.Busy = false; }
    }

    private void PublishActive()
    {
        _active = Items.Where(x => x.Active && _workers.ContainsKey(x.Manifest.Id)).Select(x => (x, _workers[x.Manifest.Id])).ToArray();
        RoutesChanged?.Invoke();
    }

    public string[] Providers(string capability) => _active.Where(x => x.Item.Manifest.Capabilities.Contains(capability)).Select(x => x.Item.Manifest.Id).ToArray();
    public async Task<PluginReply> InvokeAsync(string id, PluginRequest request, CancellationToken ct)
    {
        var entry = _active.FirstOrDefault(x => x.Item.Manifest.Id == id);
        if (entry.Worker is null) return new() { Result = PluginResult.Unavailable };
        try
        {
            var reply = await entry.Worker.InvokeAsync(request, ct);
            ct.ThrowIfCancellationRequested();
            if (!_active.Any(x => ReferenceEquals(x.Worker, entry.Worker))) return new() { Result = PluginResult.Unavailable };
            return reply;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _dispatcher?.TryEnqueue(() => { if (entry.Item.Active) entry.Item.Status = ToolUtils.GetString("PluginRequestFailed"); });
            return new() { Result = PluginResult.NetworkError };
        }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        await _changes.WaitAsync();
        try
        {
            _active = [];
            foreach (var item in Items) item.Active = false;
            RoutesChanged?.Invoke();
            foreach (var worker in _workers.Values)
                try { await worker.DisposeAsync(); } catch { /* Continue shutting down the remaining workers. */ }
            _workers.Clear();
        }
        finally { _changes.Release(); }
    }
}
