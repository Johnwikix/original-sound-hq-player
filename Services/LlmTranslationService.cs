using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Windows.Security.Credentials;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>
/// 大模型配置与歌词翻译边界。配置文件只保存端点和偏好，API 密钥由 Windows PasswordVault 保存。
/// 请求串行化并带失败冷却，避免切歌/重复打开歌词时造成请求风暴。
/// </summary>
public sealed class LlmTranslationService(ILogger<LlmTranslationService> logger, Lyrics.LyricsParser parser) : ILlmTranslationService, IDisposable
{
    // The feature has not been released; keep the service dormant until it is ready to be exposed again.
    // Existing settings and credentials remain intact, but this gate prevents any request from using them.
    private static readonly bool FeatureEnabled = false;
    private const string VaultResource = "OriginalSoundPlayer.Llm";
    private const string AnthropicVersion = "2023-06-01";
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = CreateClient();
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _failedUntil = new(StringComparer.OrdinalIgnoreCase);
    private LlmSettingsFile? _settings;
    private string? _settingsPath;
    private bool _disposed;

    public async Task<LlmSettingsFile> GetSettingsAsync(CancellationToken token = default)
    {
        await _configGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_settings is not null) return Clone(_settings);
            _settingsPath ??= GetSettingsPath();
            LlmSettingsFile? loaded = null;
            if (File.Exists(_settingsPath))
            {
                try
                {
                    if (new FileInfo(_settingsPath).Length <= 256 * 1024)
                        loaded = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_settingsPath, token).ConfigureAwait(false),
                            LlmSettingsJsonContext.Default.LlmSettingsFile);
                }
                catch (JsonException ex)
                {
                    PreserveCorrupt(_settingsPath);
                    logger.LogWarning(ex, "读取大模型配置失败，将使用默认配置");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning(ex, "读取大模型配置失败，将使用默认配置"); }
            }
            _settings = Normalize(loaded ?? new());
            return Clone(_settings);
        }
        finally { _configGate.Release(); }
    }

    public async Task SaveAsync(LlmSettingsFile settings, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LlmSettingsFile normalized = Normalize(settings);
        string path = _settingsPath ??= GetSettingsPath();
        await _configGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(normalized, LlmSettingsJsonContext.Default.LlmSettingsFile);
                await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), token).ConfigureAwait(false);
                File.Move(temporary, path, true);
                _settings = normalized;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _configGate.Release(); }
    }

    public async Task<string?> GetApiKeyAsync(string profileId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var credential = new PasswordVault().Retrieve(VaultResource, profileId);
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password) ? null : credential.Password;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Exception && ex.HResult == unchecked((int)0x80070490))
        {
            return null;
        }
    }

    public Task SetApiKeyAsync(string profileId, string? apiKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profileId)) throw new ArgumentException("Profile id is required.", nameof(profileId));
        var vault = new PasswordVault();
        try
        {
            var old = vault.Retrieve(VaultResource, profileId);
            vault.Remove(old);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Exception && ex.HResult == unchecked((int)0x80070490)) { }
        if (!string.IsNullOrWhiteSpace(apiKey))
            vault.Add(new PasswordCredential(VaultResource, profileId, apiKey.Trim()));
        return Task.CompletedTask;
    }

    public Task<string?> TranslateAsync(Music music, LyricsDocument document, CancellationToken token = default)
        => TranslateCoreAsync(music, document, force: false, trigger: "自动", token);

    /// <summary>手动翻译允许覆盖现有译文，并忽略自动失败冷却，便于用户立即重试。</summary>
    public Task<string?> TranslateManuallyAsync(Music music, LyricsDocument document, CancellationToken token = default)
        => TranslateCoreAsync(music, document, force: true, trigger: "手动", token);

    private async Task<string?> TranslateCoreAsync(Music music, LyricsDocument document, bool force, string trigger,
        CancellationToken token)
    {
        if (!FeatureEnabled) return null;
        logger.LogInformation("大模型歌词翻译触发: {Trigger}, 曲目: {Title}", trigger, music.Title);
        if (_disposed || document.Original.Content.Length == 0 || (!force && !string.IsNullOrWhiteSpace(document.TranslationLrc))) return null;
        LlmSettingsFile settings = await GetSettingsAsync(token).ConfigureAwait(false);
        LlmProviderProfile? profile = settings.Profiles.FirstOrDefault(x => x is not null && x.Id == settings.ActiveProfileId)
            ?? settings.Profiles.FirstOrDefault(x => x is not null);
        if (profile is null || !profile.Enabled || string.IsNullOrWhiteSpace(profile.Model)) return null;
        string key = music.Id > 0 ? $"id:{music.Id}" : music.Path;
        if (!force && _failedUntil.TryGetValue(key, out long until) && until > DateTimeOffset.UtcNow.Ticks) return null;
        string? apiKey = await GetApiKeyAsync(profile.Id, token).ConfigureAwait(false);
        if (profile.Protocol == LlmApiProtocol.Anthropic && string.IsNullOrWhiteSpace(apiKey)) return null;
        await _requestGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string sourceLrc;
            try { sourceLrc = parser.NormalizeTranslation(document.Original.Content, token); }
            catch (FormatException) { sourceLrc = document.Original.Content; }
            string prompt = BuildPrompt(profile.TargetLanguage, music, sourceLrc);
            string result = profile.Protocol == LlmApiProtocol.Anthropic
                ? await CallAnthropicAsync(profile, apiKey, prompt, token).ConfigureAwait(false)
                : await CallOpenAiAsync(profile, apiKey, prompt, token).ConfigureAwait(false);
            result = StripCodeFence(result);
            string normalized = parser.NormalizeTranslation(result, token);
            if (string.IsNullOrWhiteSpace(normalized)) throw new FormatException("The model returned an empty translation.");
            _failedUntil.TryRemove(key, out _);
            return normalized;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _failedUntil[key] = DateTimeOffset.UtcNow.Add(FailureCooldown).Ticks;
            if (_failedUntil.Count > 256)
            {
                long now = DateTimeOffset.UtcNow.Ticks;
                foreach (var item in _failedUntil)
                    if (item.Value <= now) _failedUntil.TryRemove(item.Key, out _);
            }
            logger.LogWarning(ex, "大模型歌词翻译失败: {Title}", music.Title);
            return null;
        }
        finally { _requestGate.Release(); }
    }

    private static async Task<string> CallOpenAiAsync(LlmProviderProfile profile, string? apiKey, string prompt, CancellationToken token)
    {
        var body = new LlmOpenAiRequest
        {
            Model = profile.Model,
            Messages =
            [
                new LlmChatMessage { Role = "system", Content = "You are a precise song lyric translator." },
                new LlmChatMessage { Role = "user", Content = prompt }
            ],
            Temperature = 0.2
        };
        if (profile.ThinkingLevel is "low" or "medium" or "high") body.ReasoningEffort = profile.ThinkingLevel;
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(profile, "chat/completions"))
        { Content = new StringContent(JsonSerializer.Serialize(body, LlmRequestJsonContext.Default.LlmOpenAiRequest), Encoding.UTF8, "application/json") };
        ApplyHeaders(request, profile, apiKey);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        string text = await ReadBoundedAsync(response, token).ConfigureAwait(false);
        EnsureSuccess(response, text);
        using JsonDocument json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static async Task<string> CallAnthropicAsync(LlmProviderProfile profile, string? apiKey, string prompt, CancellationToken token)
    {
        var body = new LlmAnthropicRequest
        {
            Model = profile.Model,
            MaxTokens = profile.ThinkingLevel switch { "high" => 8192, "medium" => 4096, "low" => 2048, _ => 4096 },
            Messages = [new LlmChatMessage { Role = "user", Content = prompt }]
        };
        if (profile.ThinkingLevel is "low" or "medium" or "high")
            body.Thinking = new LlmAnthropicThinking
            {
                BudgetTokens = profile.ThinkingLevel switch { "high" => 4096, "medium" => 2048, _ => 1024 }
            };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(profile, "messages"))
        { Content = new StringContent(JsonSerializer.Serialize(body, LlmRequestJsonContext.Default.LlmAnthropicRequest), Encoding.UTF8, "application/json") };
        ApplyHeaders(request, profile, apiKey);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        string text = await ReadBoundedAsync(response, token).ConfigureAwait(false);
        EnsureSuccess(response, text);
        using JsonDocument json = JsonDocument.Parse(text);
        var output = new StringBuilder();
        if (json.RootElement.TryGetProperty("content", out JsonElement blocks))
            foreach (JsonElement block in blocks.EnumerateArray())
                if (block.TryGetProperty("text", out JsonElement value)) output.Append(value.GetString());
        return output.ToString();
    }

    private static string BuildPrompt(string targetLanguage, Music music, string lrc)
    {
        string instructions = targetLanguage.Trim().ToLowerInvariant() switch
        {
            "中文" or "简体中文" or "繁體中文" or "繁体中文" or "chinese" =>
                $"请将下面的带时间戳歌词翻译成{targetLanguage}。逐行翻译，保留每个时间戳一次且顺序不变，只返回 [mm:ss.fff]译文 的 LRC 行，不要 Markdown、解释、音译或元数据。纯音乐空行可以省略。",
            "日本語" or "日语" or "japanese" =>
                $"次のタイムスタンプ付き歌詞を{targetLanguage}へ自然に翻訳してください。各タイムスタンプを一度だけ同じ順序で保持し、[mm:ss.fff]訳文 のLRC行だけを返してください。Markdown、説明、ローマ字表記、メタデータは不要です。",
            "한국어" or "韩语" or "korean" =>
                $"아래의 타임스탬프 가사를 {targetLanguage}로 자연스럽게 번역하세요. 모든 타임스탬프를 같은 순서로 한 번씩 유지하고 [mm:ss.fff]번역문 형식의 LRC 줄만 반환하세요. 설명, 마크다운, 로마자 표기, 메타데이터는 쓰지 마세요.",
            "español" or "西班牙语" or "spanish" =>
                $"Traduce estas letras sincronizadas al {targetLanguage} de forma natural. Conserva cada marca de tiempo exactamente una vez y en el mismo orden. Devuelve únicamente líneas LRC [mm:ss.fff]traducción, sin Markdown, explicaciones, transliteración ni metadatos.",
            "русский" or "俄语" or "russian" =>
                $"Естественно переведи эти синхронизированные строки на {targetLanguage}. Сохрани каждую временную метку ровно один раз и в том же порядке. Верни только строки LRC вида [mm:ss.fff]перевод, без Markdown, пояснений, транслитерации и метаданных.",
            "deutsch" or "德语" or "german" =>
                $"Übersetze den folgenden synchronisierten Liedtext natürlich ins {targetLanguage}. Jede Zeitmarke muss genau einmal und in derselben Reihenfolge erhalten bleiben. Gib ausschließlich LRC-Zeilen im Format [mm:ss.fff]Übersetzung aus, ohne Markdown, Erklärungen, Transliteration oder Metadaten.",
            _ =>
                $"Translate the following synchronized song lyrics into {targetLanguage}. Keep every timestamp exactly once and in the same order. Return only valid LRC lines in [mm:ss.fff]text format, without Markdown fences, explanations, transliteration, or metadata."
        };
        return $"{instructions}\nTitle: {music.Title}\nArtist: {music.Author}\n\n{lrc}";
    }

    private static string StripCodeFence(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLine = text.IndexOf('\n');
            int end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && end > firstLine) text = text[(firstLine + 1)..end];
        }
        return text.Trim();
    }

    private static Uri BuildUri(LlmProviderProfile profile, string path)
    {
        string baseUrl = profile.BaseUrl.TrimEnd('/');
        string normalizedPath = path.TrimStart('/');
        if (profile.Protocol == LlmApiProtocol.Anthropic)
        {
            // Anthropic-compatible gateways commonly expose the API at /anthropic,
            // while the wire endpoints remain /anthropic/v1/messages.
            // Official Anthropic URLs already include /v1; do not add it twice.
            Uri baseUri = new(baseUrl + "/");
            string pathPart = baseUri.AbsolutePath.TrimEnd('/');
            if (!pathPart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                baseUrl += "/v1";
        }
        return new Uri($"{baseUrl}/{normalizedPath}", UriKind.Absolute);
    }
    private static void ApplyHeaders(HttpRequestMessage request, LlmProviderProfile profile, string? apiKey)
    {
        if (profile.Protocol == LlmApiProtocol.Anthropic)
        {
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new FormatException("LLM response is too large.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        char[] buffer = new char[16 * 1024];
        var output = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            output.Append(buffer, 0, read);
            if (output.Length > 4 * 1024 * 1024) throw new FormatException("LLM response is too large.");
        }
        return output.ToString();
    }

    private static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(120) };
    private static string GetSettingsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Settings", "Llm.json");
    private static void PreserveCorrupt(string path)
    {
        try { if (File.Exists(path)) File.Move(path, $"{path}.corrupt-{Guid.NewGuid():N}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode) return;
        string message = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement detail) && detail.ValueKind == JsonValueKind.String)
                    message += $": {detail.GetString()}";
                else if (error.ValueKind == JsonValueKind.String)
                    message += $": {error.GetString()}";
            }
            else if (json.RootElement.TryGetProperty("message", out JsonElement rootMessage) && rootMessage.ValueKind == JsonValueKind.String)
                message += $": {rootMessage.GetString()}";
        }
        catch (JsonException) { }
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static LlmSettingsFile Normalize(LlmSettingsFile value)
    {
        value.Profiles ??= [];
        if (value.Profiles.Count == 0) value.Profiles.Add(new());
        for (int index = 0; index < value.Profiles.Count; index++)
        {
            value.Profiles[index] ??= new();
            NormalizeProfile(value.Profiles[index]);
        }
        if (value.Profiles.All(x => x.Id != value.ActiveProfileId)) value.ActiveProfileId = value.Profiles[0].Id;
        value.SchemaVersion = 1;
        return value;
    }

    private static void NormalizeProfile(LlmProviderProfile profile)
    {
        profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim();
        profile.BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? profile.Protocol == LlmApiProtocol.Anthropic ? "https://api.anthropic.com/v1" : "https://api.openai.com/v1"
            : profile.BaseUrl.Trim();
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("API 地址必须是 http 或 https URL。");
        profile.Protocol = LlmProtocolDetection.Detect(profile.BaseUrl, profile.Protocol);
        profile.Model = profile.Model?.Trim() ?? "";
        profile.TargetLanguage = string.IsNullOrWhiteSpace(profile.TargetLanguage) ? "简体中文" : profile.TargetLanguage.Trim();
        profile.ThinkingLevel = profile.ThinkingLevel is "off" or "low" or "medium" or "high" ? profile.ThinkingLevel : "auto";
    }
    private static LlmSettingsFile Clone(LlmSettingsFile source) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(source, LlmSettingsJsonContext.Default.LlmSettingsFile), LlmSettingsJsonContext.Default.LlmSettingsFile)!;
    public void Dispose() { _disposed = true; }
}

