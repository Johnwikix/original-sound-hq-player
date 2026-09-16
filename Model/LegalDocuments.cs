using System;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model;

public sealed record LegalSection(string Title, string[] Paragraphs, bool Important = false);
public sealed record LegalDocument(string Id, string Title, string Summary, LegalSection[] Sections);
public sealed record LegalBundle(string Version, string Language, LegalDocument[] Documents);
public sealed record AgreementReceipt(string Version, string Language, DateTimeOffset AcceptedAtUtc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LegalBundle))]
[JsonSerializable(typeof(AgreementReceipt))]
internal partial class LegalJsonContext : JsonSerializerContext;
