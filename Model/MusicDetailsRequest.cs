using System;

namespace WinUIMusicPlayer.Model;

/// <summary>详情的纯数据入口。null 来源表示沿用当前队列；条目标识区分重复入队。</summary>
public sealed record MusicDetailsRequest(Music Music, Type? PlaybackPage, long QueueEntryId = 0);
