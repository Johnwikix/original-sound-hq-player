using System.Collections.Generic;
using System;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model;

/// <summary>协议族；绝大多数第三方服务都提供 OpenAI 兼容入口。</summary>
public enum LlmApiProtocol
{
    OpenAi,
    Anthropic
}

public static class LlmProtocolDetection
{
    public static LlmApiProtocol Detect(string? baseUrl, LlmApiProtocol fallback = LlmApiProtocol.OpenAi)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)) return fallback;
        if (uri.Host.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/anthropic", StringComparison.OrdinalIgnoreCase))
            return LlmApiProtocol.Anthropic;
        return LlmApiProtocol.OpenAi;
    }
}

public sealed class LlmProviderProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "OpenAI compatible";
    public LlmApiProtocol Protocol { get; set; } = LlmApiProtocol.OpenAi;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string TargetLanguage { get; set; } = "简体中文";
    public string ThinkingLevel { get; set; } = "auto";
    public bool Enabled { get; set; }
    public bool SaveTranslationFile { get; set; } = true;
}

public sealed class LlmSettingsFile
{
    public int SchemaVersion { get; set; } = 1;
    public string ActiveProfileId { get; set; } = "default";
    public List<LlmProviderProfile> Profiles { get; set; } = [new()];
}

/// <summary>源生成的请求 DTO，避免发布配置中使用反射序列化。</summary>
internal sealed class LlmChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal sealed class LlmOpenAiRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<LlmChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }
}

internal sealed class LlmAnthropicThinking
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "enabled";

    [JsonPropertyName("budget_tokens")]
    public int BudgetTokens { get; set; }
}

internal sealed class LlmAnthropicRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("messages")]
    public List<LlmChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmAnthropicThinking? Thinking { get; set; }
}

[JsonSerializable(typeof(LlmSettingsFile))]
[JsonSerializable(typeof(LlmProviderProfile))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class LlmSettingsJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(LlmOpenAiRequest))]
[JsonSerializable(typeof(LlmAnthropicRequest))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class LlmRequestJsonContext : JsonSerializerContext { }
