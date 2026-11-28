using Lyricify.Lyrics.Helpers;

namespace Lyricify.Lyrics.Searchers
{
    public class KugouSearcher : Searcher, ISearcher
    {
        public override string Name => "Kugou";

        public override string DisplayName => "Kugou Music";

        public override Searchers SearcherType => Searchers.Kugou;

        public override Task<List<ISearchResult>?> SearchForResults(string searchString)
            => SearchForResults(searchString, CancellationToken.None);

        public override async Task<List<ISearchResult>?> SearchForResults(string searchString, CancellationToken cancellationToken)
        {
            var result = await ProviderHelper.KugouApi.GetSearchSong(searchString, cancellationToken);
            if (result?.Status != 1 || result.ErrorCode != 0 || result.Data is null)
                throw new InvalidOperationException("酷狗歌曲搜索服务返回失败状态。");
            var results = result.Data.Info;
            if (results is null && result.Data.Total > 0)
                throw new InvalidOperationException("酷狗搜索响应缺少歌曲列表。");

            var search = new List<ISearchResult>();
            foreach (var track in results ?? [])
            {
                AddCandidate(track);
                if (track.Group is { } group)
                    foreach (var subTrack in group)
                        AddCandidate(subTrack);
            }
            return search;

            void AddCandidate(Providers.Web.Kugou.SearchSongResponse.DataItem.InfoItem candidate)
            {
                if (!string.IsNullOrWhiteSpace(candidate.Hash) && !string.IsNullOrWhiteSpace(candidate.SongName) &&
                    !string.IsNullOrWhiteSpace(candidate.SingerName))
                    search.Add(new KugouSearchResult(candidate));
            }
        }
    }
}
