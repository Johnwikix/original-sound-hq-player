namespace WinUIMusicPlayer.Services
{
    // Only metadata lookup is substituted. Decode, cache leases, processes and filesystem remain real.
    public sealed record RemoteTrack(int SourceId, string Href, long Length, string ETag);
    public sealed class MusicDatabaseService
    {
        public RemoteTrack? Track { get; set; }
        public Task<RemoteTrack?> GetRemoteTrackAsync(int id) => Task.FromResult(Track);
    }
    public static class WebDavLibraryService
    {
        public static WebDav.WebDavEntry ToEntry(RemoteTrack track) => new(track.Href, "", false, track.Length, track.ETag, null);
    }
}
namespace WinUIMusicPlayer.Services.WebDav
{
    public sealed record WebDavEntry(string Href, string Name, bool IsDirectory, long Length, string ETag, DateTimeOffset? Modified);
}
