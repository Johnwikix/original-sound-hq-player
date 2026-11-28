using Lyricify.Lyrics.Serialization;
using System.Text;
using System.Text.Json.Serialization.Metadata;

namespace Lyricify.Lyrics.Providers.Web
{
    public abstract class BaseApi
    {
        public static HttpClient HttpClient = new();

        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36";

        public const string Cookie = "os=pc;osver=Microsoft-Windows-10-Professional-build-16299.125-64bit;appver=2.0.3.131777;channel=netease;__remember_me=true";

        protected abstract string? HttpRefer { get; }

        protected abstract Dictionary<string, string>? AdditionalHeaders { get; }

        protected virtual string? HttpCookie => null;

        protected Task<string> GetAsync(string url) => GetAsync(url, CancellationToken.None);

        protected Task<string> GetAsync(string url, CancellationToken cancellationToken)
            => SendAsync(HttpMethod.Get, url, null, cancellationToken);

        protected Task<string> PostAsync(string url, Dictionary<string, string> paramDict)
            => PostAsync(url, paramDict, CancellationToken.None);

        protected Task<string> PostAsync(string url, Dictionary<string, string> paramDict, CancellationToken cancellationToken)
            => SendAsync(HttpMethod.Post, url, new FormUrlEncodedContent(paramDict), cancellationToken);

        protected Task<string> PostJsonAsync(string url, object param)
            => PostAsync(url, LyricsJson.Serialize(param));

        protected Task<string> PostAsync(string url, Dictionary<string, object> paramDict)
            => PostAsync(url, paramDict, CancellationToken.None);

        protected Task<string> PostAsync(string url, Dictionary<string, object> paramDict, CancellationToken cancellationToken)
            => SendAsync(HttpMethod.Post, url, new StringContent(LyricsJson.Serialize(paramDict), Encoding.UTF8, "application/json"), cancellationToken);

        protected Task<string> PostAsync(string url, string param)
            => SendAsync(HttpMethod.Post, url, new StringContent(param, Encoding.UTF8, "application/json"), CancellationToken.None);

        private async Task<string> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            cancellationToken.ThrowIfCancellationRequested();
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (!string.IsNullOrEmpty(HttpRefer))
                request.Headers.TryAddWithoutValidation("Referer", HttpRefer);
            if (!string.IsNullOrEmpty(HttpCookie))
                request.Headers.TryAddWithoutValidation("Cookie", HttpCookie);

            if (AdditionalHeaders is { } additional)
            {
                foreach (var pair in additional)
                    request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }

    public static class JsonUtils
    {
        public static T? ToEntity<T>(this string val, JsonTypeInfo<T> typeInfo) => LyricsJson.Deserialize(val, typeInfo);

        public static List<T>? ToEntityList<T>(this string val, JsonTypeInfo<List<T>> typeInfo) => LyricsJson.Deserialize(val, typeInfo);

        public static string ToJson<T>(this T entity, JsonTypeInfo<T> typeInfo) => LyricsJson.Serialize(entity, typeInfo);

        public static T? ToEntity<T>(this string val) => LyricsJson.Deserialize<T>(val);

        public static List<T>? ToEntityList<T>(this string val) => LyricsJson.Deserialize<List<T>>(val);

        public static string? ToJson<T>(this T entity, bool writeIndented = false) => LyricsJson.Serialize(entity, writeIndented);
    }
}
