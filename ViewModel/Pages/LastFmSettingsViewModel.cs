using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using System.Threading.Tasks;
using Launcher = Windows.System.Launcher;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.LastFm;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel.Pages;

public sealed class LastFmSettingsViewModel : ObservableObject
{
    private readonly LastFmService _service;
    private readonly AppLifecycle _lifecycle;
    private LastFmStatus _status = new();
    private DispatcherQueue? _dispatcher;
    private string _apiKey = "", _secret = "", _message = "";
    private bool _enabled, _busy, _stopped, _includeOlderHistory;
    private Task _operation = Task.CompletedTask;

    public LastFmSettingsViewModel(LastFmService service, AppLifecycle lifecycle, ILastFmHistorySource? history = null)
    {
        _service = service;
        _lifecycle = lifecycle;
        SaveCredentialsCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await service.ConfigureAsync(ApiKey, SharedSecret);
            SharedSecret = "";
        }), () => CanInteract && _status.IsReady && ApiKey.Trim().Length == 32
            && (SharedSecret.Trim().Length == 32 || (_status.HasSecret && ApiKey.Trim() == _status.ApiKey)));
        AuthorizeCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            Uri uri = await service.BeginAuthorizationAsync();
            try
            {
                if (!await Launcher.LaunchUriAsync(uri)) _message = ToolUtils.GetString("LastFmBrowserError");
            }
            catch (Exception) { _message = ToolUtils.GetString("LastFmBrowserError"); }
        }), () => CanInteract && _status.IsReady && _status.HasSecret);
        CompleteAuthorizationCommand = new AsyncRelayCommand(() => RunAsync(service.CompleteAuthorizationAsync),
            () => CanInteract && _status.AuthorizationPending);
        DisconnectCommand = new AsyncRelayCommand(() => RunAsync(service.DisconnectAsync),
            () => CanInteract && (_status.IsAuthenticated || _status.UserName.Length != 0 || _status.AuthorizationPending));
        RetryCommand = new AsyncRelayCommand(() => RunAsync(() => _status.IsReady ? service.RetryAsync() : service.StartAsync()),
            () => CanInteract);
        bool CanImportHistory() => CanInteract && history is not null && _status.IsAuthenticated && _status.Enabled;
        ImportHistoryCommand = new AsyncRelayCommand(token => CanImportHistory()
            ? RunAsync(() => service.ImportHistoryAsync(history!, IncludeOlderHistory, token)) : Task.CompletedTask, CanImportHistory);
        CancelHistoryImportCommand = new RelayCommand(ImportHistoryCommand.Cancel, () => ImportHistoryCommand.CanBeCanceled);
        ImportHistoryCommand.PropertyChanged += (_, _) => CancelHistoryImportCommand.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand SaveCredentialsCommand { get; }
    public IAsyncRelayCommand AuthorizeCommand { get; }
    public IAsyncRelayCommand CompleteAuthorizationCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand ImportHistoryCommand { get; }
    public IRelayCommand CancelHistoryImportCommand { get; }
    public bool CanInteract => !_busy && !_stopped && !_lifecycle.StoppingToken.IsCancellationRequested;
    public bool CanToggle => CanInteract && _status.IsAuthenticated;
    public bool IsBusy => _busy;
    public string ApiKey { get => _apiKey; set { if (SetProperty(ref _apiKey, value)) NotifyCommands(); } }
    public string SharedSecret { get => _secret; set { if (SetProperty(ref _secret, value)) NotifyCommands(); } }
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (CanToggle && SetProperty(ref _enabled, value)) _ = RunAsync(() => _service.SetEnabledAsync(value));
        }
    }

    public bool IncludeOlderHistory
    {
        get => _includeOlderHistory;
        set { if (CanInteract) SetProperty(ref _includeOlderHistory, value); }
    }

    public string SecretHint => ToolUtils.GetString(_status.HasSecret ? "LastFmSecretSaved" : "LastFmSecretRequired");
    public string AccountText => _status.IsAuthenticated
        ? string.Format(CultureInfo.CurrentCulture, ToolUtils.GetString("LastFmConnected"), _status.UserName)
        : ToolUtils.GetString("LastFmNotConnected");
    public string QueueText => string.Format(CultureInfo.CurrentCulture, ToolUtils.GetString("LastFmPendingCount"), _status.PendingCount);
    public string HistoryResultText => _status.HistoryImport is { } result
        ? string.Format(CultureInfo.CurrentCulture, ToolUtils.GetString("LastFmHistoryResult"),
            result.Queued, result.AlreadyPresent, result.TooOld, result.Ineligible, result.Deferred, result.Retimed, result.Uncertain)
        : "";
    public InfoBarSeverity Severity => _message.Length != 0 || _status.Problem is not (LastFmProblem.None or LastFmProblem.AuthorizationPending)
        ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
    public string StatusText => _message.Length != 0 ? _message : _busy ? ToolUtils.GetString("LastFmWorking")
        : _status.Problem != LastFmProblem.None ? ToolUtils.GetString("LastFmError" + _status.Problem)
        : _status.AuthorizationPending ? ToolUtils.GetString("LastFmAuthorizationHelp")
        : ToolUtils.GetString("LastFmUsageHelp");

    public void Start(DispatcherQueue dispatcher)
    {
        if (_dispatcher is not null || _stopped) return;
        _dispatcher = dispatcher;
        _service.Changed += Changed;
        _lifecycle.Changed += LifecycleChanged;
        Apply(_service.Status);
    }

    private void Changed(LastFmStatus status) => _dispatcher?.TryEnqueue(() =>
    {
        if (!_stopped && !_lifecycle.StoppingToken.IsCancellationRequested) Apply(status);
    });
    private void LifecycleChanged(object? sender, EventArgs args) => _dispatcher?.TryEnqueue(NotifyCommands);

    private void Apply(LastFmStatus status)
    {
        if (_apiKey.Length == 0 || _apiKey == _status.ApiKey) ApiKey = status.ApiKey;
        if (_status.ApiKey != status.ApiKey || !string.Equals(_status.UserName, status.UserName, StringComparison.OrdinalIgnoreCase)
            || (_status.IsAuthenticated && !status.IsAuthenticated))
            SetProperty(ref _includeOlderHistory, false, nameof(IncludeOlderHistory));
        _status = status;
        SetProperty(ref _enabled, status.Enabled, nameof(Enabled));
        OnPropertyChanged(nameof(SecretHint));
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(HistoryResultText));
        NotifyCommands();
    }

    private Task RunAsync(Func<Task> action)
    {
        if (!CanInteract) return Task.CompletedTask;
        return _operation = RunCoreAsync(action);
    }

    private async Task RunCoreAsync(Func<Task> action)
    {
        _busy = true;
        _message = "";
        NotifyCommands();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Service publishes a localized problem; never display credentials or raw HTTP errors. */ }
        finally
        {
            _busy = false;
            if (!_stopped && !_lifecycle.StoppingToken.IsCancellationRequested) Apply(_service.Status);
        }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(StatusText));
        SaveCredentialsCommand.NotifyCanExecuteChanged();
        AuthorizeCommand.NotifyCanExecuteChanged();
        CompleteAuthorizationCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        ImportHistoryCommand.NotifyCanExecuteChanged();
    }

    public async Task StopAsync()
    {
        _stopped = true;
        ImportHistoryCommand.Cancel();
        _service.Changed -= Changed;
        _lifecycle.Changed -= LifecycleChanged;
        await _operation;
        SharedSecret = "";
    }
}
