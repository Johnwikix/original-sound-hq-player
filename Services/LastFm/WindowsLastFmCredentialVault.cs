using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;

namespace WinUIMusicPlayer.Services.LastFm;

public sealed class WindowsLastFmCredentialVault : ILastFmCredentialVault
{
    private const string Resource = "OriginalSoundPlayer.LastFm";
    private const string Key = "Identity";
    public Task<string?> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, Key);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { return null; }
    }, token);

    public Task WriteAsync(string? value, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var vault = new PasswordVault();
        if (value is not null) vault.Add(new PasswordCredential(Resource, Key, value));
        else
        {
            try { vault.Remove(vault.Retrieve(Resource, Key)); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { }
        }
    }, token);
}
