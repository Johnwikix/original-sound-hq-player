using Microsoft.UI.Composition;
using System.Numerics;
using System.Runtime.CompilerServices;
using WinUIMusicPlayer.Helper.Animations;

internal sealed partial class RegressionApp
{
    private async Task CheckCompositionCacheAsync()
    {
        using var owner = new Compositor();
        using var otherOwner = new Compositor();
        int creations = 0;
        var first = owner.GetCached("ReuseProbe", () => { creations++; return new object(); });
        Check(ReferenceEquals(first, owner.GetCached("ReuseProbe", () => { creations++; return new object(); })) && creations == 1,
            "A live compositor reuses the same cached object without invoking the factory again");
        Check(!ReferenceEquals(first, otherOwner.GetCached("ReuseProbe", static () => new object())),
            "Different compositors keep separate cache entries for the same key");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => owner.GetCached("ConcurrentProbe", static () => new object()))));
        Check(concurrent.All(value => ReferenceEquals(value, concurrent[0])),
            "Concurrent managed cache callers observe the same published entry");
        try
        {
            owner.GetCached<object>("FailureProbe", static () => throw new InvalidOperationException("factory failure"));
            throw new Exception("Factory failure was swallowed");
        }
        catch (InvalidOperationException exception) when (exception.Message == "factory failure") { }
        var recovered = owner.GetCached("FailureProbe", static () => new object());
        Check(ReferenceEquals(recovered, owner.GetCached("FailureProbe", static () => new object())),
            "A failed cache factory remains retryable");

        using var visual = owner.CreateSpriteVisual();
        using var secondVisual = owner.CreateSpriteVisual();
        var ownerAnimation = owner.GetCached("OwnerAnimationProbe", owner.CreateScalarKeyFrameAnimation);
        Check(ReferenceEquals(ownerAnimation, visual.GetCached("OwnerAnimationProbe", owner.CreateScalarKeyFrameAnimation)),
            "CompositionObject lookups resolve to their owner's existing cache");
        var down = (Vector3KeyFrameAnimation)FluentAnimationHelper.CreatePointerDown(visual, 0.85f);
        Check(ReferenceEquals(down, FluentAnimationHelper.CreatePointerDown(secondVisual, 0.85f))
            && down.Target == nameof(Visual.Scale) && down.Duration == TimeSpan.FromSeconds(0.1) && down.KeyFrameCount == 1,
            "Pressed animations retain their cache reuse, Scale target, duration and keyframe count");
        Check(!ReferenceEquals(down, FluentAnimationHelper.CreatePointerDown(visual, 0.9f)),
            "Different pressed scales still use different cached animations");
        var up = (SpringVector3NaturalMotionAnimation)FluentAnimationHelper.CreatePointerUp(visual);
        Check(ReferenceEquals(up, FluentAnimationHelper.CreatePointerUp(secondVisual))
            && up.Target == nameof(Visual.Scale) && up.FinalValue == Vector3.One && Math.Abs(up.DampingRatio - 0.4f) < 0.0001f,
            "Release animations retain their cache reuse, Scale target, final value and spring damping");
        var centering = CompositionFactory.StartCentering(visual);
        Check(ReferenceEquals(centering, CompositionFactory.StartCentering(secondVisual))
            && centering.Target == nameof(Visual.CenterPoint) && centering.Expression.Contains("this.Target.Size"),
            "Centering expressions remain reusable across visuals without capturing a specific target");

        var retainedValue = CacheOwnerReference(owner);
        var abandoned = new (WeakReference Owner, WeakReference Entry, WeakReference Animation)[20];
        for (int i = 0; i < abandoned.Length; i++) abandoned[i] = CreateAbandonedCompositor();
        // 只在生命周期回归中诱导 GC；覆盖缓存值反向引用键以及真实 WinRT 动画对象的情形。
        for (int pass = 0; pass < 5; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        Check(retainedValue.IsAlive, "A live compositor keeps its cache value alive across GC");
        int retainedOwners = abandoned.Count(entry => entry.Owner.IsAlive);
        int retainedEntries = abandoned.Count(entry => entry.Entry.IsAlive || entry.Animation.IsAlive);
        Check(retainedOwners == 0 && retainedEntries == 0,
            $"Abandoned compositor graphs are collectible: owners={retainedOwners}/20, cache values={retainedEntries}/20");
        GC.KeepAlive(owner);

        Func<object> factory = static () => new object();
        for (int i = 0; i < 2000; i++) owner.GetCached("AllocationProbe", factory);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) owner.GetCached("AllocationProbe", factory);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, $"100000 warmed cache hits allocate {allocated} managed bytes with a reused factory delegate");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CacheOwnerReference(Compositor owner)
        => new(owner.GetCached("OwnerReferenceProbe", () => new CachedOwner(owner)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Entry, WeakReference Animation) CreateAbandonedCompositor()
    {
        using var compositor = new Compositor();
        var entry = compositor.GetCached("BackReferenceProbe", () => new CachedOwner(compositor));
        var animation = compositor.GetCached("NativeAnimationProbe", compositor.CreateScalarKeyFrameAnimation);
        return (new(compositor), new(entry), new(animation));
    }

    /// <summary>显式反向引用 Compositor，防止只弱化键但强持有值的缓存通过测试。</summary>
    private sealed class CachedOwner(Compositor owner)
    {
        public Compositor Owner { get; } = owner;
    }
}
