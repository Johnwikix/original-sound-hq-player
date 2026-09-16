using AnimatedWin2dControls.Impressionist;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Helper;

/// <summary>按主题和算法缓存最终结果。并发首次加载合并；失败和取消不进入缓存。</summary>
internal sealed class DefaultCoverPaletteCache(
    Func<bool, PaletteAlgorithm, CancellationToken, Task<PaletteResult?>> load)
{
    private readonly Dictionary<(bool IsDark, PaletteAlgorithm Algorithm), PaletteResult> _palettes = new();
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public ValueTask<PaletteResult?> GetAsync(bool isDark, PaletteAlgorithm algorithm, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = (isDark, algorithm);
        lock (_sync)
        {
            if (_palettes.TryGetValue(key, out var cached)) return ValueTask.FromResult<PaletteResult?>(cached);
        }
        return LoadAsync(key, token);
    }

    private async ValueTask<PaletteResult?> LoadAsync((bool IsDark, PaletteAlgorithm Algorithm) key, CancellationToken token)
    {
        await _loadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_palettes.TryGetValue(key, out var cached)) return cached;
            }
            var palette = await load(key.IsDark, key.Algorithm, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (palette is not null)
            {
                // 消费者只读 PaletteResult，不修改其中的颜色列表。
                lock (_sync) _palettes.Add(key, palette);
            }
            return palette;
        }
        finally { _loadGate.Release(); }
    }
}
