using Lyricify.Lyrics.Providers.Web.QQMusic;

namespace Lyricify.Lyrics.Searchers
{
    public class QQMusicSearcher : Searcher, ISearcher
    {
        public override string Name => "QQ Music";

        public override string DisplayName => "QQ Music";

        public override Searchers SearcherType => Searchers.QQMusic;

        public override async Task<List<ISearchResult>?> SearchForResults(string searchString)
        {
            // 请求异常直接向上抛出（网络/服务故障），与"搜索成功但无结果"区分
            var result = await Providers.Web.Providers.QQMusicApi.Search(searchString, Api.SearchTypeEnum.SONG_ID);
            var results = result?.Req_1?.Data?.Body?.Song?.List;
            if (results == null) return null;

            var search = new List<ISearchResult>();
            try
            {
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
            }
            catch
            {
                return null;
            }

            return search;
        }
    }
}
