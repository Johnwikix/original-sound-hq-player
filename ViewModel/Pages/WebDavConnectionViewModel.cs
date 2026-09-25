using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
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
    private WebDavConnection? _tested;
    private CancellationTokenSource? _requestCancel;
    private CancellationTokenSource _treeCancel = CancellationTokenSource.CreateLinkedTokenSource(library.StoppingToken);
    private readonly HashSet<string> _savedRoots = new(original?.Roots.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [], StringComparer.Ordinal);
    private readonly HashSet<Task> _treeLoads = [];
    private int _revision;
    private WebDavCertificate? _certificate;
    private WebDavCertificateTrust? _trust = string.IsNullOrEmpty(original?.TrustedCertificateSha256) ? null
        : new(original.TrustedCertificateOrigin, original.TrustedCertificateSha256);
    public string Name { get; set => SetProperty(ref field, value); } = original?.Name ?? "";
    public string Address
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            _trust = null;
            InvalidateConnection();
        }
    } = original?.BaseUri ?? "";
    public string UserName { get; set { if (SetProperty(ref field, value)) InvalidateConnection(); } } = original?.UserName ?? "";
    public string Password { get; set { if (SetProperty(ref field, value) && !_disposed) InvalidateConnection(); } } = "";
    public bool ScanOnStartup { get; set => SetProperty(ref field, value); } = original?.ScanOnStartup ?? false;
    public bool ReadMetadata { get; set => SetProperty(ref field, value); } = original?.ReadMetadata ?? true;
    public bool Enabled { get; set => SetProperty(ref field, value); } = original?.Enabled ?? true;
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    public bool HasCertificate => _certificate is not null;
    public bool HasTrustedCertificate => _trust is not null;
    public string CertificateDetails { get; private set => SetProperty(ref field, value); } = "";
    public bool IsConnecting
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) return;
            TrustCertificateCommand.NotifyCanExecuteChanged();
            ForgetCertificateCommand.NotifyCanExecuteChanged();
        }
    }
    public ObservableCollection<WebDavTreeItem> Folders { get; } = [];
    public bool HasFolders => Folders.Count != 0;
    private void InvalidateConnection()
    {
        _revision++;
        _requestCancel?.Cancel();
        var previousTreeCancel = _treeCancel;
        previousTreeCancel.Cancel();
        if (_treeLoads.Count == 0) previousTreeCancel.Dispose();
        else _ = DisposeTreeCancelAfterLoadsAsync(previousTreeCancel, [.. _treeLoads]);
        _treeCancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _tested = null;
        SetCertificate(null);
        Folders.Clear();
        OnPropertyChanged(nameof(HasFolders));
        Status = "";
        OnPropertyChanged(nameof(HasTrustedCertificate));
        ForgetCertificateCommand.NotifyCanExecuteChanged();
    }
    private static async Task DisposeTreeCancelAfterLoadsAsync(CancellationTokenSource cancel, Task[] loads)
    {
        try { await Task.WhenAll(loads); }
        catch (OperationCanceledException) { }
        catch { /* 目录加载入口已报告错误，清理仍须继续。 */ }
        finally { cancel.Dispose(); }
    }
    private void SetCertificate(WebDavCertificate? certificate)
    {
        _certificate = certificate;
        CertificateDetails = certificate is null ? "" : string.Format(ToolUtils.GetString("WebDavCertificateDetails"),
            certificate.Origin, certificate.Subject, certificate.Issuer, certificate.NotBefore.LocalDateTime,
            certificate.NotAfter.LocalDateTime, certificate.Sha256,
            string.Join("\n", new[]
            {
                certificate.NameMismatch ? ToolUtils.GetString("WebDavCertificateNameMismatch") : "",
                certificate.ChainErrors ? ToolUtils.GetString("WebDavCertificateChainError") : "",
                !certificate.CanTrust ? ToolUtils.GetString("WebDavCertificateExpired") : ""
            }).Trim());
        OnPropertyChanged(nameof(HasCertificate));
        TrustCertificateCommand.NotifyCanExecuteChanged();
    }
    private WebDavConnection Connection()
    {
        string password = Password;
        if (original is not null && password.Length == 0 && original.UserName == UserName) password = library.Connect(original).Password;
        return new(WebDavTransport.NormalizeRoot(Address), UserName, password, _trust);
    }
    [RelayCommand]
    private async Task ConnectAsync()
    {
        int revision = _revision;
        using var requestCancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _requestCancel = requestCancel;
        IsConnecting = true;
        try
        {
            _tested = null;
            SetCertificate(null);
            Folders.Clear();
            OnPropertyChanged(nameof(HasFolders));
            Status = ToolUtils.GetString("WebDavConnecting");
            var connection = Connection();
            var root = new WebDavTreeItem(connection.Root.AbsolutePath, ToolUtils.GetString("WebDavEntireRoot"), true)
            { IsScanRoot = original is null || _savedRoots.Count == 0 || HasSavedRoot(connection.Root.AbsolutePath), IsExpanded = true };
            await LoadChildrenCoreAsync(root, connection, requestCancel.Token);
            if (original is not null) await RevealSavedRootsAsync(root, connection, requestCancel.Token);
            if (_disposed || revision != _revision) return;
            Folders.Add(root);
            RefreshSelection();
            OnPropertyChanged(nameof(HasFolders));
            _tested = connection;
            Status = ToolUtils.GetString("WebDavConnected");
        }
        catch (OperationCanceledException) { }
        catch (WebDavCertificateException ex)
        {
            if (_disposed || revision != _revision) return;
            SetCertificate(ex.Certificate);
            Status = WebDavText.Error(ex.Code);
        }
        catch (Exception ex)
        {
            if (!_disposed && revision == _revision) Status = WebDavText.Error(ex is WebDavException dav ? dav.Code : "ConnectionFailed");
        }
        finally { _requestCancel = null; IsConnecting = false; }
    }
    private bool HasSavedRoot(string href) => _savedRoots.Contains(href);

    private async Task RevealSavedRootsAsync(WebDavTreeItem folder, WebDavConnection connection, CancellationToken token)
    {
        foreach (var child in folder.Children)
        {
            bool hasDescendant = false;
            foreach (var savedRoot in _savedRoots)
                if (savedRoot.StartsWith(child.Href, StringComparison.Ordinal) && savedRoot != child.Href)
                { hasDescendant = true; break; }
            child.IsScanRoot = HasSavedRoot(child.Href);
            if (!hasDescendant) continue;
            child.IsExpanded = true;
            await LoadChildrenCoreAsync(child, connection, token);
            await RevealSavedRootsAsync(child, connection, token);
        }
    }

    public async Task LoadChildrenAsync(WebDavTreeItem folder)
    {
        if (_disposed || _tested is not { } connection || folder.IsLoaded || folder.IsLoading) return;
        int revision = _revision;
        try
        {
            var work = LoadChildrenCoreAsync(folder, connection, _treeCancel.Token);
            _treeLoads.Add(work);
            try { await work; }
            finally { _treeLoads.Remove(work); }
            if (_disposed || revision != _revision) return;
            RefreshSelection();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && revision == _revision)
                Status = WebDavText.Error(ex is WebDavException dav ? dav.Code : "ConnectionFailed");
        }
    }

    private async Task LoadChildrenCoreAsync(WebDavTreeItem folder, WebDavConnection connection, CancellationToken token)
    {
        if (folder.IsLoaded || folder.IsLoading) return;
        folder.IsLoading = true;
        try
        {
            var children = new List<WebDavTreeItem>();
            await foreach (var entry in transport.ListAsync(connection, folder.Href, token))
                if (entry.IsDirectory) children.Add(new(entry.Href, entry.Name, true) { Parent = folder });
            token.ThrowIfCancellationRequested();
            foreach (var child in children) folder.Children.Add(child);
            folder.IsLoaded = true;
        }
        finally { folder.IsLoading = false; }
    }

    /// <summary>级联选择由模型维护，父节点的部分选择不会扩大持久化的扫描范围。</summary>
    public void SetSelected(WebDavTreeItem folder, bool selected)
    {
        if (_disposed) return;
        if (!selected)
        {
            // 从选中的祖先里排除一个子树时，以沿途的兄弟目录保留其余范围。
            // Roots 只表达包含关系，不能同时保留祖先根又排除其中的子目录。
            var selectedAncestor = folder.Parent;
            while (selectedAncestor is not null && !selectedAncestor.IsScanRoot) selectedAncestor = selectedAncestor.Parent;
            if (selectedAncestor is not null)
            {
                var branch = folder;
                while (branch.Parent is { } parent)
                {
                    parent.IsScanRoot = false;
                    foreach (var sibling in parent.Children)
                        if (!ReferenceEquals(sibling, branch)) sibling.IsScanRoot = true;
                    if (ReferenceEquals(parent, selectedAncestor)) break;
                    branch = parent;
                }
            }
        }
        ClearScanRoots(folder);
        folder.IsScanRoot = selected;
        RefreshSelection();
    }
    private static void ClearScanRoots(WebDavTreeItem folder)
    {
        folder.IsScanRoot = false;
        foreach (var child in folder.Children) ClearScanRoots(child);
    }
    private void RefreshSelection()
    {
        foreach (var root in Folders) RefreshSelection(root, false);
    }
    private static void RefreshSelection(WebDavTreeItem folder, bool inherited)
    {
        bool selected = inherited || folder.IsScanRoot;
        bool hasSelectedChild = false;
        foreach (var child in folder.Children)
        {
            RefreshSelection(child, selected);
            hasSelectedChild |= child.SelectionState != false;
        }
        folder.IsSelected = selected;
        folder.SelectionState = selected ? true : hasSelectedChild ? null : false;
    }
    private bool CanTrustCertificate() => !_disposed && !IsConnecting && _certificate is { CanTrust: true };
    [RelayCommand(CanExecute = nameof(CanTrustCertificate))]
    private async Task TrustCertificateAsync()
    {
        if (!CanTrustCertificate() || _certificate is not { } certificate) return;
        if (Connection().Root.GetLeftPart(UriPartial.Authority) != certificate.Origin) return;
        _trust = new(certificate.Origin, certificate.Sha256);
        OnPropertyChanged(nameof(HasTrustedCertificate));
        ForgetCertificateCommand.NotifyCanExecuteChanged();
        await ConnectCommand.ExecuteAsync(null);
    }
    private bool CanForgetCertificate() => !_disposed && !IsConnecting && _trust is not null;
    [RelayCommand(CanExecute = nameof(CanForgetCertificate))]
    private void ForgetCertificate()
    {
        if (!CanForgetCertificate()) return;
        _trust = null;
        InvalidateConnection();
    }
    public async Task<bool> SaveAsync()
    {
        try
        {
            if (_disposed) return false;
            var connection = Connection();
            if (_tested != connection) { Status = ToolUtils.GetString("WebDavTestFirst"); return false; }
            var roots = new List<string>();
            foreach (var folder in Folders) CollectSelectedRoots(folder, false, roots);
            if (roots.Count == 0) { Status = ToolUtils.GetString("WebDavChooseFolder"); return false; }
            if (roots.Contains(connection.Root.AbsolutePath)) { roots.Clear(); roots.Add(connection.Root.AbsolutePath); }
            var source = new WebDavSource
            {
                Id = original?.Id ?? 0, Name = Name.Trim(), BaseUri = connection.Root.AbsoluteUri, UserName = UserName,
                Roots = string.Join('\n', roots), ScanOnStartup = ScanOnStartup, ReadMetadata = ReadMetadata, Enabled = Enabled,
                TrustedCertificateOrigin = _trust?.Origin ?? "", TrustedCertificateSha256 = _trust?.Sha256 ?? ""
            };
            if (original is not null) { source.CredentialKey = original.CredentialKey; await library.CancelScanAsync(original.Id); }
            await library.SaveSourceAsync(source, connection.Password);
            _ = library.ScanAsync(source);
            return true;
        }
        catch (Exception ex) { Status = WebDavText.Error(ex is WebDavException dav ? dav.Code : "ConnectionFailed"); return false; }
    }
    private static void CollectSelectedRoots(WebDavTreeItem folder, bool selectedAncestor, List<string> roots)
    {
        if (folder.IsScanRoot && !selectedAncestor) roots.Add(folder.Href);
        foreach (var child in folder.Children) CollectSelectedRoots(child, selectedAncestor || folder.IsScanRoot, roots);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _treeCancel.Cancel();
        Password = "";
        _tested = null;
        _ = DrainAsync();
    }
    private async Task DrainAsync()
    {
        try
        {
            if (ConnectCommand.ExecutionTask is { } task) await task;
            await Task.WhenAll([.. _treeLoads]);
        }
        catch (OperationCanceledException) { }
        finally { _treeCancel.Dispose(); _stop.Dispose(); }
    }
}
