using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lyricify.Lyrics.Serialization;

namespace Lyricify.Lyrics.Providers.Web.QQMusic
{
    public partial class Api
    {
        private const string SongSearchUrl = "https://u6.y.qq.com/cgi-bin/musicu.fcg";
        private const string SearchModule = "music.search.SearchCgiService";
        private const string MobileSearchMethod = "DoSearchForQQMusicMobile";
        private const string MobileRequestKey = SearchModule + "." + MobileSearchMethod;
        private const string MobileClientVersion = "20080508";

        // Keep the mobile song-search contract from upstream 77e7acbc. The content type
        // is form-urlencoded even though the endpoint expects a JSON payload.
        public async Task<Song[]> SearchMobile(string keyword, CancellationToken cancellationToken)
        {
            var data = new Dictionary<string, object>
            {
                ["comm"] = new Dictionary<string, object>
                {
                    ["ct"] = "11",
                    ["v"] = MobileClientVersion,
                    ["tmeLoginType"] = "0",
                    ["tmeLoginMethod"] = "0",
                },
                [MobileRequestKey] = new Dictionary<string, object>
                {
                    ["module"] = SearchModule,
                    ["method"] = MobileSearchMethod,
                    ["param"] = new Dictionary<string, object>
                    {
                        ["query"] = keyword,
                        ["highlight"] = 1,
                        ["searchid"] = "",
                        ["sub_searchid"] = 0,
                        ["search_type"] = 0,
                        ["nqc_flag"] = 0,
                        ["sin"] = 0,
                        ["ein"] = 30,
                        ["page_num"] = 1,
                        ["num_per_page"] = 30,
                        ["cat"] = 2,
                        ["grp"] = 1,
                        ["remoteplace"] = "",
                        ["from"] = "",
                        ["multi_zhida"] = 1,
                        ["sem"] = 0,
                        ["related_data"] = "",
                        ["ambi_data"] = "",
                    }
                }
            };
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, SongSearchUrl)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(LyricsJson.Serialize(data))),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            request.Headers.TryAddWithoutValidation("User-Agent", $"QQMusic {MobileClientVersion}(android)");
            request.Headers.Accept.ParseAdd("*/*");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.GetProperty("code").GetInt32() != 0 ||
                !root.TryGetProperty(MobileRequestKey, out var result) ||
                result.GetProperty("code").GetInt32() != 0 ||
                !result.TryGetProperty("data", out var resultData) ||
                resultData.GetProperty("code").GetInt32() != 0 ||
                !resultData.TryGetProperty("body", out var body) ||
                !body.TryGetProperty("item_song", out var songs) || songs.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("QQ Music 移动搜索返回失败或不完整的响应。");

            return songs.GetRawText().ToEntity<Song[]>()
                ?? throw new InvalidOperationException("QQ Music 移动搜索响应缺少歌曲列表。");
        }
    }
}
