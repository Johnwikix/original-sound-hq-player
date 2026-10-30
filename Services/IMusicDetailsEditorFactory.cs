using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>按已打开的曲目创建有独立草稿和生命周期的属性编辑会话。</summary>
public interface IMusicDetailsEditorFactory
{
    /// <summary>创建尚未启动读取的编辑会话。</summary>
    MusicDetailsEditorViewModel Create(Music music);
}

/// <summary>隔离属性编辑器的文件选择、封面来源及全库通知边界。</summary>
public interface IMusicDetailsEditorHost
{
    /// <summary>读取当前封面，取消后仍等待实际读取结束。</summary>
    Task<byte[]?> ReadCoverAsync(Music music, CancellationToken token);
    /// <summary>查找网络封面。</summary>
    Task<byte[]?> FindCoverAsync(Music music, CancellationToken token);
    /// <summary>让用户选择替换封面。</summary>
    Task<byte[]?> ChooseCoverAsync(CancellationToken token);
    /// <summary>导出当前封面。</summary>
    Task ExportCoverAsync(byte[] cover, CancellationToken token);
    /// <summary>打开曲目的文件位置。</summary>
    void OpenLocation(Music music);
    /// <summary>发布已提交的规范标签，刷新已有曲库投影。</summary>
    void MetadataChanged();
    /// <summary>通知文件标签写入已经排队。</summary>
    void NotifyWriteQueued(Music music);
}
