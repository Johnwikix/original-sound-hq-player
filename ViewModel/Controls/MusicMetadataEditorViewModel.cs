using CommunityToolkit.Mvvm.ComponentModel;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>拥有音乐属性的编辑草稿；取消和失败不向共享曲目发布标签。</summary>
public sealed class MusicMetadataEditorViewModel : ObservableObject
{
    private readonly Music _source;
    private AdditionalMusicMetadata _initialAdditional;

    /// <summary>获取仅供窗口编辑的曲目标签草稿。</summary>
    public Music Draft { get; }

    /// <summary>获取或设置可选信息区域是否展开；无标签时默认收起。</summary>
    public bool IsAdditionalExpanded { get => field; set => SetProperty(ref field, value); }

    /// <summary>初始化曲目属性草稿。</summary>
    public MusicMetadataEditorViewModel(Music source)
    {
        _source = source;
        Draft = Copy(source);
        _initialAdditional = AdditionalMusicMetadata.From(source);
        IsAdditionalExpanded = !_initialAdditional.IsEmpty;
    }

    /// <summary>在窗口尚未开放编辑时应用从文件补读且已入库的标签。</summary>
    /// <returns>标签值发生变化时为 <see langword="true" />；仅补齐已知空状态时为 <see langword="false" />。</returns>
    public bool ApplyLoaded(AdditionalMusicMetadata tags)
    {
        bool changed = AdditionalMusicMetadata.From(_source) != tags;
        tags.ApplyTo(_source);
        if (AdditionalMusicMetadata.From(Draft) == _initialAdditional)
        {
            tags.ApplyTo(Draft);
            _initialAdditional = tags;
            IsAdditionalExpanded = !tags.IsEmpty;
        }
        return changed;
    }

    /// <summary>生成与后续输入隔离的保存快照，未采集的空标签不会被当作清除指令。</summary>
    public Music Capture()
    {
        var snapshot = Copy(Draft);
        snapshot.AdditionalMetadataRead = Draft.AdditionalMetadataRead ||
            AdditionalMusicMetadata.From(Draft) != _initialAdditional;
        return snapshot;
    }

    /// <summary>提交成功后发布快照，并返回当前草稿是否仍与保存内容一致。</summary>
    public bool AcceptSaved(Music snapshot)
    {
        bool unchanged = Draft.Title == snapshot.Title &&
            Draft.Author == snapshot.Author &&
            Draft.Album == snapshot.Album &&
            Draft.Year == snapshot.Year &&
            Draft.TrackNumber == snapshot.TrackNumber &&
            Draft.DiskNumber == snapshot.DiskNumber &&
            AdditionalMusicMetadata.From(Draft) == AdditionalMusicMetadata.From(snapshot);
        _source.Title = snapshot.Title;
        _source.Author = snapshot.Author;
        _source.Album = snapshot.Album;
        _source.Year = snapshot.Year;
        _source.TrackNumber = snapshot.TrackNumber;
        _source.DiskNumber = snapshot.DiskNumber;
        _source.UpdateTime = snapshot.UpdateTime;
        Draft.UpdateTime = snapshot.UpdateTime;
        if (snapshot.AdditionalMetadataRead)
        {
            AdditionalMusicMetadata.From(snapshot).ApplyTo(_source);
            Draft.AdditionalMetadataRead = true;
        }
        return unchanged;
    }

    private static Music Copy(Music source)
    {
        var copy = new Music
        {
            Id = source.Id, SourceId = source.SourceId, Path = source.Path, UpdateTime = source.UpdateTime,
            Title = source.Title,
            Author = source.Author,
            Album = source.Album,
            Year = source.Year,
            TrackNumber = source.TrackNumber,
            DiskNumber = source.DiskNumber
        };
        if (source.AdditionalMetadataRead || !AdditionalMusicMetadata.From(source).IsEmpty)
            AdditionalMusicMetadata.From(source).ApplyTo(copy);
        return copy;
    }
}
