using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public sealed partial class UserAgreementViewModel : ObservableObject
{
    private readonly LegalBundle[] _bundles;
    private readonly AgreementAcceptanceStore _store;
    private int _languageIndex;
    private int _documentIndex;
    public string[] Languages { get; } = ["简体中文", "English"];
    public LegalDocument[] Documents => _bundles[LanguageIndex].Documents;
    public LegalDocument Document => Documents[Math.Clamp(DocumentIndex, 0, Documents.Length - 1)];
    public Uri DocumentUri => new($"https://johnwikix.github.io/original-sound-player-page/{Document.Id}");
    public string Version => _bundles[LanguageIndex].Version;
    public int LanguageIndex
    {
        get => _languageIndex;
        set
        {
            if (value < 0 || value >= _bundles.Length || !SetProperty(ref _languageIndex, value)) return;
            OnPropertyChanged(nameof(Documents));
            OnPropertyChanged(nameof(DocumentIndex));
            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(DocumentUri));
            OnPropertyChanged(nameof(Version));
        }
    }
    public int DocumentIndex
    {
        get => _documentIndex;
        set
        {
            if (value < 0 || value >= Documents.Length || !SetProperty(ref _documentIndex, value)) return;
            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(DocumentUri));
        }
    }
    public string Error { get => field; private set => SetProperty(ref field, value); } = "";
    public bool HasError { get => field; private set => SetProperty(ref field, value); }
    public bool IsSaving { get => field; private set => SetProperty(ref field, value); }

    internal UserAgreementViewModel(LegalBundle[] bundles, AgreementAcceptanceStore store)
    {
        _bundles = bundles;
        _store = store;
        _languageIndex = AppData.SystemLanguage == "zh" ? 0 : 1;
    }

    public static async Task<UserAgreementViewModel> LoadAsync(AgreementAcceptanceStore store)
    {
        static async Task<LegalBundle> ReadAsync(string language)
        {
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Legal", $"legal.{language}.json"));
            return await JsonSerializer.DeserializeAsync(stream, LegalJsonContext.Default.LegalBundle)
                ?? throw new InvalidDataException("Missing legal documents");
        }
        var bundles = await Task.WhenAll(ReadAsync("zh-CN"), ReadAsync("en"));
        if (bundles[0].Version != AgreementAcceptanceStore.CurrentVersion || bundles[1].Version != AgreementAcceptanceStore.CurrentVersion)
            throw new InvalidDataException("Legal document versions differ");
        return new(bundles, store);
    }

    public Task<bool> HasAcceptedAsync() => _store.HasAcceptedAsync(Version);

    public async Task<bool> AcceptAsync()
    {
        if (IsSaving) return false;
        IsSaving = true;
        HasError = false;
        try
        {
            await _store.AcceptAsync(Version, _bundles[LanguageIndex].Language);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ToolUtils.GetString("AgreementSaveFailed");
            HasError = true;
            return false;
        }
        finally { IsSaving = false; }
    }
}
