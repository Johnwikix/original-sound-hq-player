using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.ViewModel.Controls;

/// <summary>单个大模型设置对话框的状态。一个对话框拥有一个实例，关闭时不会留下后台初始化任务。</summary>
public sealed partial class LlmSettingsViewModel : ObservableObject, IDisposable
{
    private readonly LlmTranslationService _service;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _operationCts;
    private LlmSettingsFile? _settings;
    private LlmProviderProfile? _profile;
    private bool _closed;

    public LlmSettingsViewModel(LlmTranslationService service) => _service = service;

    public string[] ThinkingOptions { get; } = ["auto", "off", "low", "medium", "high"];

    private bool _isEnabled;
    private string _baseUrl = "https://api.openai.com/v1";
    private string _model = "gpt-4o-mini";
    private string _targetLanguage = "简体中文";
    private string _thinkingLevel = "auto";
    private string _apiKeyInput = "";
    private bool _apiKeyConfigured;
    private bool _isBusy;
    private string _status = "";
    private bool _saveSucceeded;

    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value ?? ""); }
    public string Model { get => _model; set => SetProperty(ref _model, value ?? ""); }
    public string TargetLanguage { get => _targetLanguage; set => SetProperty(ref _targetLanguage, value ?? ""); }
    public string ThinkingLevel { get => _thinkingLevel; set => SetProperty(ref _thinkingLevel, value ?? "auto"); }
    public string ApiKeyInput { get => _apiKeyInput; set => SetProperty(ref _apiKeyInput, value ?? ""); }

    public bool ApiKeyConfigured
    {
        get => _apiKeyConfigured;
        private set
        {
            if (SetProperty(ref _apiKeyConfigured, value)) OnPropertyChanged(nameof(ApiKeyStatusText));
        }
    }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool SaveSucceeded { get => _saveSucceeded; private set => SetProperty(ref _saveSucceeded, value); }
    public string ApiKeyStatusText => ApiKeyConfigured
        ? Utils.ToolUtils.GetString("LlmApiKeyConfigured")
        : Utils.ToolUtils.GetString("LlmApiKeyNotConfigured");

    public async Task LoadAsync(CancellationToken token = default)
    {
        await RunAsync(async operationToken =>
        {
            LlmSettingsFile settings = await _service.GetSettingsAsync(operationToken).ConfigureAwait(true);
            LlmProviderProfile profile = Active(settings);
            string? key = await _service.GetApiKeyAsync(profile.Id, operationToken).ConfigureAwait(true);
            _settings = settings;
            _profile = profile;
            IsEnabled = profile.Enabled;
            BaseUrl = profile.BaseUrl;
            Model = profile.Model;
            TargetLanguage = profile.TargetLanguage;
            ThinkingLevel = profile.ThinkingLevel;
            ApiKeyConfigured = !string.IsNullOrWhiteSpace(key);
        }, token);
    }

    public Task<bool> SaveAsync() => SaveCoreAsync();

    [RelayCommand]
    private Task SaveSettingsAsync() => SaveCoreAsync();

    private async Task<bool> SaveCoreAsync()
    {
        SaveSucceeded = false;
        await RunAsync(async token =>
        {
            LlmSettingsFile settings = _settings ?? await _service.GetSettingsAsync(token).ConfigureAwait(true);
            LlmProviderProfile profile = _profile ?? Active(settings);
            profile.BaseUrl = BaseUrl.Trim();
            profile.Protocol = LlmProtocolDetection.Detect(profile.BaseUrl, profile.Protocol);
            profile.Model = Model.Trim();
            profile.TargetLanguage = TargetLanguage.Trim();
            profile.ThinkingLevel = ThinkingLevel;
            profile.Enabled = IsEnabled;
            await _service.SaveAsync(settings, token).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(ApiKeyInput))
            {
                await _service.SetApiKeyAsync(profile.Id, ApiKeyInput, token).ConfigureAwait(true);
                ApiKeyInput = "";
                ApiKeyConfigured = true;
            }
            _settings = settings;
            _profile = profile;
            SaveSucceeded = true;
            Status = Utils.ToolUtils.GetString("LlmSaved");
        });
        return SaveSucceeded;
    }

    [RelayCommand]
    private Task ClearApiKeyAsync() => RunAsync(async token =>
    {
        LlmSettingsFile settings = _settings ?? await _service.GetSettingsAsync(token).ConfigureAwait(true);
        await _service.SetApiKeyAsync((_profile ?? Active(settings)).Id, null, token).ConfigureAwait(true);
        ApiKeyInput = "";
        ApiKeyConfigured = false;
        Status = Utils.ToolUtils.GetString("LlmApiKeyCleared");
    });

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _operationCts?.Cancel();
        // Do not dispose the semaphore here: a cancelled operation still has to
        // enter its finally block and release it before the dialog VM is collected.
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken externalToken = default)
    {
        if (_closed) return;
        await _operationGate.WaitAsync(externalToken).ConfigureAwait(true);
        CancellationTokenSource? cts = null;
        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _operationCts = cts;
            IsBusy = true;
            await operation(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts?.IsCancellationRequested == true || externalToken.IsCancellationRequested) { }
        catch (Exception ex) { Status = ex.Message; }
        finally
        {
            if (ReferenceEquals(_operationCts, cts)) _operationCts = null;
            IsBusy = false;
            cts?.Dispose();
            _operationGate.Release();
        }
    }

    private static LlmProviderProfile Active(LlmSettingsFile settings)
    {
        settings.Profiles ??= [];
        if (settings.Profiles.Count == 0) settings.Profiles.Add(new());
        LlmProviderProfile? profile = settings.Profiles.FirstOrDefault(x => x is not null && x.Id == settings.ActiveProfileId)
            ?? settings.Profiles.FirstOrDefault(x => x is not null);
        return profile ?? new LlmProviderProfile();
    }
}
