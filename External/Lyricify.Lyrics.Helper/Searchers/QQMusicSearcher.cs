using Lyricify.Lyrics.Providers.Web.QQMusic;

namespace Lyricify.Lyrics.Searchers
{
    public class QQMusicSearcher : Searcher, ISearcher
    {
        public override string Name => "QQ Music";

        public override string DisplayName => "QQ Music";

        public override Searchers SearcherType => Searchers.QQMusic;

        public override Task<List<ISearchResult>?> SearchForResults(string searchString)
            => SearchForResults(searchString, CancellationToken.None);

        public override async Task<List<ISearchResult>?> SearchForResults(string searchString, CancellationToken cancellationToken)
        {
            // 请求异常直接向上抛出（网络/服务故障），与"搜索成功但无结果"区分
            var result = await Providers.Web.Providers.QQMusicApi.Search(searchString, Api.SearchTypeEnum.SONG_ID, cancellationToken);
            if (result is null || result.Code != 0 || result.Req_1 is null || result.Req_1.Code != 0 ||
                result.Req_1.Data is null || result.Req_1.Data.Code != 0)
                throw new InvalidOperationException("QQ Music 搜索服务返回失败状态。");
            var results = result.Req_1.Data.Body?.Song?.List
                ?? throw new InvalidOperationException("QQ Music 搜索响应缺少歌曲列表。");

            var search = new List<ISearchResult>(results.Length);
            foreach (var track in results)
            {
                search.Add(new QQMusicSearchResult(track));
                if (track.Group is { Count: > 0 } group)
                {
                    foreach (var subTrack in group)
                    {
                        search.Add(new QQMusicSearchResult(subTrack));
                    }
                }
            }

            return search;
        }
    }
}
