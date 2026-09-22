using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>连接对话框的短生命周期表单；关闭时清除密码并取消连接请求。</summary>
public partial class WebDavConnectionViewModel(WebDavTransport transport, WebDavLibraryService library, WebDavSource? original = null) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(library.StoppingToken);
    private bool _disposed;
    private string? _tested;
    public string Name { get; set => SetProperty(ref field, value); } = original?.Name ?? "";
    public string Address { get; set => SetProperty(ref field, value); } = original?.BaseUri ?? "";
    public string UserName { get; set => SetProperty(ref field, value); } = original?.UserName ?? "";
    public string Password { get; set => SetProperty(ref field, value); } = "";
    public bool ScanOnStartup { get; set => SetProperty(ref field, value); } = original?.ScanOnStartup ?? false;
    public bool ReadMetadata { get; set => SetProperty(ref field, value); } = original?.ReadMetadata ?? true;
    public bool Enabled { get; set => SetProperty(ref field, value); } = original?.Enabled ?? true;
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    public ObservableCollection<WebDavFolderChoice> Folders { get; } = [];
    private WebDavConnection Connection()
    {
        string password = Password;
        if (original is not null && password.Length == 0 && original.UserName == UserName) password = library.Connect(original).Password;
        return new(WebDavTransport.NormalizeRoot(Address), UserName, password);
    }
    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            _tested = null;
            Folders.Clear();
            Status = ToolUtils.GetString("WebDavConnecting");
            var connection = Connection();
            Folders.Add(new(connection.Root.AbsolutePath, ToolUtils.GetString("WebDavEntireRoot")) { IsSelected = true });
            await foreach (var entry in transport.ListAsync(connection, connection.Root.AbsolutePath, _stop.Token))
                if (entry.IsDirectory) Folders.Add(new(entry.Href, entry.Name));
            _tested = Address + "\n" + UserName + "\n" + Password;
            if (original is not null)
                foreach (var folder in Folders) folder.IsSelected = Array.IndexOf(original.Roots.Split('\n'), folder.Href) >= 0;
            Status = ToolUtils.GetString("WebDavConnected");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = WebDavText.Error(ex is WebDavException dav ? dav.Code : "ConnectionFailed"); }
    }
    public async Task<bool> SaveAsync()
    {
        try
        {
            if (_tested != Address + "\n" + UserName + "\n" + Password) { Status = ToolUtils.GetString("WebDavTestFirst"); return false; }
            var connection = Connection();
            var roots = new System.Collections.Generic.List<string>();
            foreach (var folder in Folders) if (folder.IsSelected) roots.Add(folder.Href);
            if (roots.Count == 0) { Status = ToolUtils.GetString("WebDavChooseFolder"); return false; }
            if (roots.Contains(connection.Root.AbsolutePath)) { roots.Clear(); roots.Add(connection.Root.AbsolutePath); }
            var source = new WebDavSource
            {
                Id = original?.Id ?? 0, Name = Name.Trim(), BaseUri = connection.Root.AbsoluteUri, UserName = UserName,
                Roots = string.Join('\n', roots), ScanOnStartup = ScanOnStartup, ReadMetadata = ReadMetadata, Enabled = Enabled
            };
            if (original is not null) { source.CredentialKey = original.CredentialKey; await library.CancelScanAsync(original.Id); }
            await library.SaveSourceAsync(source, connection.Password);
            _ = library.ScanAsync(source);
            return true;
        }
        catch (Exception ex) { Status = WebDavText.Error(ex is WebDavException dav ? dav.Code : "ConnectionFailed"); return false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        Password = "";
        _tested = null;
        _ = DrainAsync();
    }
    private async Task DrainAsync()
    {
        try { if (ConnectCommand.ExecutionTask is { } task) await task; }
        catch (OperationCanceledException) { }
        finally { _stop.Dispose(); }
    }
}

public sealed class WebDavFolderChoice(string href, string name) : ObservableObject
{
    public string Href { get; } = href;
    public string Name { get; } = name;
    public bool IsSelected { get; set => SetProperty(ref field, value); }
}
