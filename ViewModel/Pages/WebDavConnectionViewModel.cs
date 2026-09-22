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
    private WebDavConnection? _tested;
    private CancellationTokenSource? _requestCancel;
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
    public string Password { get; set { if (SetProperty(ref field, value)) InvalidateConnection(); } } = "";
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
    public ObservableCollection<WebDavFolderChoice> Folders { get; } = [];
    private void InvalidateConnection()
    {
        _revision++;
        _requestCancel?.Cancel();
        _tested = null;
        SetCertificate(null);
        Folders.Clear();
        Status = "";
        OnPropertyChanged(nameof(HasTrustedCertificate));
        ForgetCertificateCommand.NotifyCanExecuteChanged();
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
            Status = ToolUtils.GetString("WebDavConnecting");
            var connection = Connection();
            var folders = new System.Collections.Generic.List<WebDavFolderChoice>
            { new(connection.Root.AbsolutePath, ToolUtils.GetString("WebDavEntireRoot")) { IsSelected = true } };
            await foreach (var entry in transport.ListAsync(connection, connection.Root.AbsolutePath, requestCancel.Token))
                if (entry.IsDirectory) folders.Add(new(entry.Href, entry.Name));
            if (_disposed || revision != _revision) return;
            foreach (var folder in folders) Folders.Add(folder);
            _tested = connection;
            if (original is not null)
                foreach (var folder in Folders) folder.IsSelected = Array.IndexOf(original.Roots.Split('\n'), folder.Href) >= 0;
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
            var roots = new System.Collections.Generic.List<string>();
            foreach (var folder in Folders) if (folder.IsSelected) roots.Add(folder.Href);
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
