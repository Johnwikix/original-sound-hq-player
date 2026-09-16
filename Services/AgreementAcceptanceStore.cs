using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>Local, versioned consent. A failed write never counts as acceptance.</summary>
public sealed class AgreementAcceptanceStore(string path)
{
    public const string CurrentVersion = "2026-09-16.1";
    public static AgreementAcceptanceStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OriginalSoundPlayer", "agreement.json"));

    public async Task<bool> HasAcceptedAsync(string version)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var receipt = await JsonSerializer.DeserializeAsync(stream, LegalJsonContext.Default.AgreementReceipt);
            return receipt is { AcceptedAtUtc: var time } && time != default && receipt.Version == version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public async Task AcceptAsync(string version, string language)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new AgreementReceipt(version, language, DateTimeOffset.UtcNow), LegalJsonContext.Default.AgreementReceipt);
                await stream.FlushAsync();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
