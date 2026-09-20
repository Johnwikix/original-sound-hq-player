using System.Text.Json.Serialization;

#nullable disable
namespace Lyricify.Lyrics.Providers.Web.SodaMusic
{
    /// <summary>
    /// Soda Music 搜索（tracks）接口返回
    /// 仅覆盖当前使用到的字段，未使用字段可按需扩展
    /// </summary>
    public class SearchResult
    {
        [JsonPropertyName("status_info")]
        public StatusInfo StatusInfo { get; set; }

        [JsonPropertyName("result_groups")]
        public List<ResultGroup> ResultGroups { get; set; }

        [JsonPropertyName("extra")]
        public Extra Extra { get; set; }
    }

    public class StatusInfo
    {
        [JsonPropertyName("log_id")]
        public string LogId { get; set; }

        /// <summary>
        /// unix 秒
        /// </summary>
        [JsonPropertyName("now")]
        public long Now { get; set; }

        /// <summary>
        /// unix 毫秒
        /// </summary>
        [JsonPropertyName("now_ts_ms")]
        public long NowTsMs { get; set; }
    }

    public class Extra
    {
        [JsonPropertyName("log_extra")]
        public string LogExtra { get; set; }
    }

    public class ResultGroup
    {
        public string Id { get; set; }

        [JsonPropertyName("next_cursor")]
        public string NextCursor { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        public List<ResultGroupItem> Data { get; set; }

        [JsonPropertyName("display_view_all")]
        public bool? DisplayViewAll { get; set; }

        [JsonPropertyName("display_title")]
        public string DisplayTitle { get; set; }

        public string Description { get; set; }
    }

    public class ResultGroupItem
    {
        public Meta Meta { get; set; }
        public Entity Entity { get; set; }
    }

    public class Meta
    {
        [JsonPropertyName("item_type")]
        public string ItemType { get; set; }
    }

    public class Entity
    {
        public TrackContainer Track { get; set; }
    }

    public class TrackContainer
    {
        public string Id { get; set; }

        public Album Album { get; set; }

        public List<Artist> Artists { get; set; }

        /// <summary>
        /// 时长(ms)
        /// </summary>
        public long Duration { get; set; }

        public string Name { get; set; }

        public Preview Preview { get; set; }

        public State State { get; set; }

        public Stats Stats { get; set; }

        public string Vid { get; set; }

        [JsonPropertyName("label_info")]
        public LabelInfo LabelInfo { get; set; }

        [JsonPropertyName("sim_id")]
        public long? SimId { get; set; }

        /// <summary>
        /// 可选码率集合（文件级）
        /// </summary>
        [JsonPropertyName("bit_rates")]
        public List<BitRateItem> BitRates { get; set; }

        [JsonPropertyName("audition_info")]
        public AuditionInfo AuditionInfo { get; set; }

        [JsonPropertyName("song_maker_team")]
        public SongMakerTeam SongMakerTeam { get; set; }

        [JsonPropertyName("media_type")]
        public string MediaType { get; set; }

        public bool? Explicit { get; set; }

        public Chorus Chorus { get; set; }

        public Colors Colors { get; set; }

        [JsonPropertyName("limited_free_info")]
        public object LimitedFreeInfo { get; set; }

        /// <summary>
        /// 1: 有人声; 2: 伴奏/翻唱等
        /// </summary>
        public int? Vocal { get; set; }

        [JsonPropertyName("lang_codes")]
        public List<string> LangCodes { get; set; }

        [JsonPropertyName("first_vocal")]
        public Range FirstVocal { get; set; }

        [JsonPropertyName("sharable_platforms")]
        public List<string> SharablePlatforms { get; set; }

        /// <summary>
        /// 可播放区间（ms）
        /// </summary>
        [JsonPropertyName("playable_range")]
        public Range PlayableRange { get; set; }

        [JsonPropertyName("plug_status")]
        public PlugStatus PlugStatus { get; set; }

        public List<TagWrapper> Tags { get; set; }

        public List<Fragment> Fragments { get; set; }
    }

    public class Album
    {
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// unix 秒
        /// </summary>
        [JsonPropertyName("release_date")]
        public long ReleaseDate { get; set; }

        [JsonPropertyName("url_cover")]
        public UrlWithTemplate UrlCover { get; set; }

        [JsonPropertyName("url_player_bg")]
        public UrlWithTemplate UrlPlayerBg { get; set; }

        [JsonPropertyName("cover_gradient_effect_color")]
        public List<RgbColor> CoverGradientEffectColor { get; set; }

        [JsonPropertyName("playing_wave_color")]
        public RgbaColor PlayingWaveColor { get; set; }

        [JsonPropertyName("paused_wave_color")]
        public RgbaColor PausedWaveColor { get; set; }
    }

    public class UrlWithTemplate
    {
        public string Uri { get; set; }

        public List<string> Urls { get; set; }

        [JsonPropertyName("template")]
        public string Template { get; set; }

        [JsonPropertyName("template_prefix")]
        public string TemplatePrefix { get; set; }
    }

    public class Artist
    {
        public string Id { get; set; }

        public string Name { get; set; }

        [JsonPropertyName("url_avatar")]
        public UrlWithTemplate UrlAvatar { get; set; }

        public State State { get; set; }

        [JsonPropertyName("user_info")]
        public ArtistUserInfo UserInfo { get; set; }

        [JsonPropertyName("simple_display_name")]
        public string SimpleDisplayName { get; set; }

        [JsonPropertyName("user_artist_type")]
        public int? UserArtistType { get; set; }
    }

    public class ArtistUserInfo
    {
        public string Id { get; set; }

        public string Nickname { get; set; }

        [JsonPropertyName("medium_avatar_url")]
        public UrlsOnly MediumAvatarUrl { get; set; }

        [JsonPropertyName("thumb_avatar_url")]
        public UrlsOnly ThumbAvatarUrl { get; set; }

        [JsonPropertyName("artist_id")]
        public string ArtistId { get; set; }

        public bool? Secret { get; set; }

        [JsonPropertyName("test_tag")]
        public int? TestTag { get; set; }

        [JsonPropertyName("vip_stage")]
        public string VipStage { get; set; }

        [JsonPropertyName("is_vip")]
        public bool? IsVip { get; set; }
    }

    public class UrlsOnly
    {
        public List<string> Urls { get; set; }

        [JsonPropertyName("need_complete_url")]
        public bool? NeedCompleteUrl { get; set; }
    }

    public class State
    {
        [JsonPropertyName("blocked_by_me")]
        public bool? BlockedByMe { get; set; }
    }

    public class Preview
    {
        /// <summary>
        /// 试听时长(ms) 或有时是固定 30047/29002
        /// </summary>
        public int? Duration { get; set; }

        /// <summary>
        /// 试听起点(ms)
        /// </summary>
        public int? Start { get; set; }

        public string Vid { get; set; }

        [JsonPropertyName("bit_rates")]
        public List<BitRateItem> BitRates { get; set; }
    }

    public class BitRateItem
    {
        /// <summary>
        /// 比特率（br），单位 bps
        /// </summary>
        public long Br { get; set; }

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// 质量标识：medium/higher/highest/lossless/spatial
        /// </summary>
        public string Quality { get; set; }
    }

    public class Stats
    {
        [JsonPropertyName("count_collected")]
        public long? CountCollected { get; set; }

        [JsonPropertyName("count_comment")]
        public long? CountComment { get; set; }

        [JsonPropertyName("count_shared")]
        public long? CountShared { get; set; }
    }

    public class LabelInfo
    {
        [JsonPropertyName("only_vip_download")]
        public bool? OnlyVipDownload { get; set; }

        [JsonPropertyName("only_vip_playable")]
        public bool? OnlyVipPlayable { get; set; }

        [JsonPropertyName("quality_only_vip_can_download")]
        public List<string> QualityOnlyVipCanDownload { get; set; }

        [JsonPropertyName("quality_only_vip_can_play")]
        public List<string> QualityOnlyVipCanPlay { get; set; }

        [JsonPropertyName("is_original")]
        public bool? IsOriginal { get; set; }

        [JsonPropertyName("quality_map")]
        public Dictionary<string, QualityDetailWrap> QualityMap { get; set; }
    }

    public class QualityDetailWrap
    {
        [JsonPropertyName("play_detail")]
        public QualityDetail PlayDetail { get; set; }

        [JsonPropertyName("download_detail")]
        public QualityDetail DownloadDetail { get; set; }
    }

    public class QualityDetail
    {
        public string Condition { get; set; }

        [JsonPropertyName("need_vip")]
        public bool? NeedVip { get; set; }

        [JsonPropertyName("need_purchase")]
        public bool? NeedPurchase { get; set; }
    }

    public class AuditionInfo
    {
        public string Vid { get; set; }

        [JsonPropertyName("start_time_ms")]
        public int? StartTimeMs { get; set; }

        [JsonPropertyName("duration_ms")]
        public int? DurationMs { get; set; }
    }

    public class SongMakerTeam
    {
        public List<NameOnly> Composers { get; set; }
        public List<NameOnly> Lyricists { get; set; }
    }

    public class NameOnly
    {
        public string Name { get; set; }
    }

    public class Chorus
    {
        /// <summary>
        /// 开始(ms)
        /// </summary>
        public int? Start { get; set; }

        /// <summary>
        /// 时长(ms)
        /// </summary>
        public int? Duration { get; set; }
    }

    public class Colors
    {
        [JsonPropertyName("cover_gradient_effect_color")]
        public List<RgbColor> CoverGradientEffectColor { get; set; }

        [JsonPropertyName("normal_lyric_color")]
        public RgbaColor NormalLyricColor { get; set; }

        [JsonPropertyName("playing_lyric_color")]
        public RgbaColor PlayingLyricColor { get; set; }

        [JsonPropertyName("recommend_reason_background_color")]
        public RgbaColor RecommendReasonBackgroundColor { get; set; }

        [JsonPropertyName("featured_comment_tag_color")]
        public RgbaColor FeaturedCommentTagColor { get; set; }

        [JsonPropertyName("background_color")]
        public RgbaColor BackgroundColor { get; set; }

        [JsonPropertyName("playing_wave_color")]
        public RgbaColor PlayingWaveColor { get; set; }

        [JsonPropertyName("paused_wave_color")]
        public RgbaColor PausedWaveColor { get; set; }

        [JsonPropertyName("comment_share_additional_color")]
        public RgbaColor CommentShareAdditionalColor { get; set; }

        [JsonPropertyName("base_colors")]
        public List<RgbColor> BaseColors { get; set; }

        [JsonPropertyName("non_interactive_anchor_background")]
        public RgbaColor NonInteractiveAnchorBackground { get; set; }
    }

    public class RgbColor
    {
        public string Rgb { get; set; }
    }

    public class RgbaColor : RgbColor
    {
        public string Alpha { get; set; }
    }

    public class Range
    {
        /// <summary>
        /// 开始(ms)
        /// </summary>
        public int? Start { get; set; }

        /// <summary>
        /// 时长(ms)
        /// </summary>
        public int? Duration { get; set; }
    }

    public class PlugStatus
    {
        [JsonPropertyName("can_plug")]
        public bool? CanPlug { get; set; }

        [JsonPropertyName("is_plugged")]
        public bool? IsPlugged { get; set; }
    }

    public class TagWrapper
    {
        public TagCategory Category { get; set; }

        [JsonPropertyName("first_level_tag")]
        public Tag FirstLevelTag { get; set; }

        [JsonPropertyName("second_level_tag")]
        public Tag SecondLevelTag { get; set; }
    }

    public class TagCategory
    {
        [JsonPropertyName("tag_id")]
        public long TagId { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }
    }

    public class Tag
    {
        [JsonPropertyName("tag_id")]
        public long TagId { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }
    }

    public class Fragment
    {
        public string Type { get; set; }

        [JsonPropertyName("start_time")]
        public int? StartTime { get; set; }

        [JsonPropertyName("end_time")]
        public int? EndTime { get; set; }
    }

    /// <summary>
    /// Soda Music track_v2 接口返回（获取乐曲具体信息）
    /// </summary>
    public class TrackDetailResult
    {
        [JsonPropertyName("status_info")]
        public StatusInfo StatusInfo { get; set; }

        public LyricInfo Lyric { get; set; }

        public TrackInfo Track { get; set; }

        [JsonPropertyName("track_player")]
        public TrackPlayer TrackPlayer { get; set; }

        [JsonPropertyName("risk_result")]
        public int? RiskResult { get; set; }

        [JsonPropertyName("expire_at")]
        public long? ExpireAt { get; set; }
    }

    public class LyricInfo
    {
        public string Content { get; set; }

        public string Lang { get; set; }

        [JsonPropertyName("hide_request_lyrics")]
        public bool? HideRequestLyrics { get; set; }

        public string Type { get; set; }

        [JsonPropertyName("lyric_contributor")]
        public LyricContributor LyricContributor { get; set; }

        public string Id { get; set; }

        [JsonPropertyName("lang_translations")]
        public Dictionary<string, LyricTranslation> LangTranslations { get; set; }
    }

    public class LyricContributor
    {
        [JsonPropertyName("filter_reason")]
        public string FilterReason { get; set; }
    }

    public class LyricTranslation
    {
        public string Content { get; set; }

        public string Lang { get; set; }

        [JsonPropertyName("hide_request_lyrics")]
        public bool? HideRequestLyrics { get; set; }

        public string Type { get; set; }

        [JsonPropertyName("lyric_contributor")]
        public LyricContributor LyricContributor { get; set; }

        public string Id { get; set; }
    }

    public class TrackInfo
    {
        public string Id { get; set; }

        public Album Album { get; set; }

        public List<Artist> Artists { get; set; }

        /// <summary>
        /// 时长(ms)
        /// </summary>
        public long Duration { get; set; }

        public string Name { get; set; }

        public Preview Preview { get; set; }

        public State State { get; set; }

        public Stats Stats { get; set; }

        public string Vid { get; set; }

        [JsonPropertyName("label_info")]
        public LabelInfo LabelInfo { get; set; }

        [JsonPropertyName("sim_id")]
        public long? SimId { get; set; }

        [JsonPropertyName("bit_rates")]
        public List<BitRateItem> BitRates { get; set; }

        [JsonPropertyName("audition_info")]
        public AuditionInfo AuditionInfo { get; set; }

        [JsonPropertyName("song_maker_team")]
        public SongMakerTeam SongMakerTeam { get; set; }

        [JsonPropertyName("media_type")]
        public string MediaType { get; set; }

        public Chorus Chorus { get; set; }

        public Colors Colors { get; set; }

        [JsonPropertyName("limited_free_info")]
        public LimitedFreeInfo LimitedFreeInfo { get; set; }

        public int? Vocal { get; set; }

        [JsonPropertyName("lang_codes")]
        public List<string> LangCodes { get; set; }

        [JsonPropertyName("first_vocal")]
        public Range FirstVocal { get; set; }

        [JsonPropertyName("sharable_platforms")]
        public List<string> SharablePlatforms { get; set; }

        [JsonPropertyName("plug_status")]
        public PlugStatus PlugStatus { get; set; }

        public List<TagWrapper> Tags { get; set; }
    }

    public class LimitedFreeInfo
    {
        [JsonPropertyName("queue_types")]
        public List<string> QueueTypes { get; set; }

        [JsonPropertyName("expire_time")]
        public long? ExpireTime { get; set; }

        public string Sign { get; set; }

        [JsonPropertyName("sign_version")]
        public string SignVersion { get; set; }

        [JsonPropertyName("limited_free_type")]
        public string LimitedFreeType { get; set; }

        [JsonPropertyName("rewind_prev_intercept_type")]
        public string RewindPrevInterceptType { get; set; }

        [JsonPropertyName("intercept_type")]
        public string InterceptType { get; set; }
    }

    public class TrackPlayer
    {
        [JsonPropertyName("expire_at")]
        public long? ExpireAt { get; set; }

        [JsonPropertyName("media_id")]
        public string MediaId { get; set; }

        [JsonPropertyName("url_player_info")]
        public string UrlPlayerInfo { get; set; }

        [JsonPropertyName("video_model")]
        public string VideoModel { get; set; }

        [JsonPropertyName("video_model_type")]
        public int? VideoModelType { get; set; }
    }
}
