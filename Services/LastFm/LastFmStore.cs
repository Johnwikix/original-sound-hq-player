using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

public interface ILastFmStore
{
    Task<LastFmSavedState> LoadAsync(CancellationToken token);
    Task SaveAsync(LastFmSavedState state, CancellationToken token);
    Task<LastFmIdentity?> ReadIdentityAsync(CancellationToken token);
    Task SaveIdentityAsync(LastFmIdentity? identity, CancellationToken token);
}

public interface ILastFmCredentialVault
{
    Task<string?> ReadAsync(CancellationToken token);
    Task WriteAsync(string? value, CancellationToken token);
}

/// <summary>One atomic queue/preferences file; the entire identity stays in PasswordVault.</summary>
public sealed class LastFmStore(ILastFmCredentialVault vault, string path) : ILastFmStore
{
    private readonly string _path = path;
    public LastFmStore(ILastFmCredentialVault vault) : this(vault,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Settings", "LastFm.json")) { }

    public Task<LastFmSavedState> LoadAsync(CancellationToken token) => Task.Run(async () =>
    {
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > 160 * 1024 * 1024) throw new IOException("Last.fm queue is too large.");
        var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_path, token).ConfigureAwait(false),
            LastFmJsonContext.Default.LastFmSavedState) ?? throw new JsonException();
        if (state.Version != 1 || state.Pending is null || state.Pending.Count > 10000) throw new JsonException();
        foreach (var play in state.Pending)
            if (play is null || play.Id == Guid.Empty || play.Track is null || string.IsNullOrEmpty(play.ApiKey) || string.IsNullOrEmpty(play.Account)
                || string.IsNullOrWhiteSpace(play.Track.Title) || string.IsNullOrWhiteSpace(play.Track.Artist)
                || play.Track.Album is null || play.StartedAt <= 0) throw new JsonException();
        return state;
    }, token);

    public Task SaveAsync(LastFmSavedState state, CancellationToken token) => Task.Run(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, LastFmJsonContext.Default.LastFmSavedState), token).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    public async Task<LastFmIdentity?> ReadIdentityAsync(CancellationToken token)
    {
        string? value = await vault.ReadAsync(token).ConfigureAwait(false);
        if (value is null) return null;
        var identity = JsonSerializer.Deserialize(value, LastFmJsonContext.Default.LastFmIdentity);
        if (identity is not null && (identity.Credentials is not { ApiKey.Length: 32, SharedSecret.Length: 32 }
            || identity.Account is { Name: null } or { SessionKey: null })) throw new JsonException();
        return identity;
    }

    public Task SaveIdentityAsync(LastFmIdentity? identity, CancellationToken token) => vault.WriteAsync(
        identity is null ? null : JsonSerializer.Serialize(identity, LastFmJsonContext.Default.LastFmIdentity), token);
}
