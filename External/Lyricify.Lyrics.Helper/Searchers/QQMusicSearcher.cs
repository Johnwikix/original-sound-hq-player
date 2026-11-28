using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Providers.Web.QQMusic;

namespace Lyricify.Lyrics.Searchers
{
    public class QQMusicSearcher : Searcher, ISearcher
    {
        private readonly bool _preferMobile;

        public QQMusicSearcher() { }

        public QQMusicSearcher(bool preferMobile) => _preferMobile = preferMobile;

        public override string Name => "QQ Music";

        public override string DisplayName => "QQ Music";

        public override Searchers SearcherType => Searchers.QQMusic;

        public override Task<List<ISearchResult>?> SearchForResults(string searchString)
            => SearchForResults(searchString, CancellationToken.None);

        public override async Task<List<ISearchResult>?> SearchForResults(string searchString, CancellationToken cancellationToken)
        {
            var api = ProviderHelper.QQMusicApi;
            if (_preferMobile)
            {
                try
                {
                    var mobileResults = ToCandidates(await api.SearchMobile(searchString, cancellationToken));
                    if (mobileResults.Count > 0)
                        return mobileResults;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                    ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    // 移动端接口异常时尝试桌面端，最终失败由调用方保留重试。
                }
            }

            var result = _preferMobile
                ? await api.SearchDesktopForLyrics(searchString, cancellationToken)
                : await api.Search(searchString, Api.SearchTypeEnum.SONG_ID, cancellationToken);
            if (result is null || result.Code != 0 || result.Req_1 is null || result.Req_1.Code != 0 ||
                result.Req_1.Data is null || result.Req_1.Data.Code != 0)
                throw new InvalidOperationException("QQ Music 搜索服务返回失败状态。");
            var results = result.Req_1.Data.Body?.Song?.List
                ?? throw new InvalidOperationException("QQ Music 搜索响应缺少歌曲列表。");

            return ToCandidates(results);
        }

        private static List<ISearchResult> ToCandidates(Song[] songs)
        {
            var search = new List<ISearchResult>(songs.Length);
            foreach (var track in songs)
            {
                AddCandidate(track);
                if (track.Group is { Count: > 0 } group)
                    foreach (var subTrack in group)
                        AddCandidate(subTrack);
            }
            return search;

            void AddCandidate(Song song)
            {
                if (!string.IsNullOrWhiteSpace(song.Id) && !string.IsNullOrWhiteSpace(song.Mid) &&
                    !string.IsNullOrWhiteSpace(song.Title ?? song.Name))
                    search.Add(new QQMusicSearchResult(song));
            }
        }
    }
}
