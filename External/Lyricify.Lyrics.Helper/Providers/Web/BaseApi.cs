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

        protected Task<string> GetAsync(string url) => GetAsync(url, CancellationToken.None);

        protected async Task<string> GetAsync(string url, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetRequestHeaders();

            using var response = await HttpClient.GetAsync(url, cancellationToken);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync(cancellationToken);

            return result;
        }

        protected Task<string> PostAsync(string url, Dictionary<string, string> paramDict) => PostAsync(url, paramDict, CancellationToken.None);

        protected async Task<string> PostAsync(string url, Dictionary<string, string> paramDict, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetRequestHeaders();

            using var content = new FormUrlEncodedContent(paramDict);
            using var response = await HttpClient.PostAsync(url, content, cancellationToken);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync(cancellationToken);

            return result;
        }

        protected async Task<string> PostJsonAsync(string url, object param)
        {
            SetRequestHeaders();

            var content = new StringContent(LyricsJson.Serialize(param), Encoding.UTF8, "application/json");

            var response = await HttpClient.PostAsync(url, content);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync();

            return result;
        }

        protected Task<string> PostAsync(string url, Dictionary<string, object> paramDict) => PostAsync(url, paramDict, CancellationToken.None);

        protected async Task<string> PostAsync(string url, Dictionary<string, object> paramDict, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetRequestHeaders();

            using var jsonContent = new StringContent(paramDict.ToJson(), Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(url, jsonContent, cancellationToken);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync(cancellationToken);

            return result;
        }

        protected async Task<string> PostAsync(string url, string param)
        {
            SetRequestHeaders();

            var jsonContent = new StringContent(param, Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(url, jsonContent);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync();

            return result;
        }

        private void SetRequestHeaders()
        {
            HttpClient.DefaultRequestHeaders.Clear();

            if (!string.IsNullOrEmpty(UserAgent))
                HttpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            if (!string.IsNullOrEmpty(HttpRefer))
                HttpClient.DefaultRequestHeaders.Add("Referer", HttpRefer);
            if (!string.IsNullOrEmpty(Cookie))
                HttpClient.DefaultRequestHeaders.Add("Cookie", Cookie);

            if (AdditionalHeaders is not null)
            {
                foreach (var pair in AdditionalHeaders)
                {
                    HttpClient.DefaultRequestHeaders.Add(pair.Key, pair.Value);
                }
            }
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
