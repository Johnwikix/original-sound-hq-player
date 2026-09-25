using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace AudioPlayer.Playback;

/// <summary>Large network rings own committed pages instead of per-track LOH arrays.
/// The ring lock serializes all copies with Dispose; SafeHandle covers abandoned construction.
/// No Span escapes a copy, and KeepAlive prevents finalization during native memory access.</summary>
internal sealed unsafe class AudioRingMemory<T> : SafeHandleZeroOrMinusOneIsInvalid where T : unmanaged
{
    private readonly int _length;

    public AudioRingMemory(int length) : base(true)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        _length = length;
        SetHandle(AudioRingAllocation.VirtualAlloc(0, checked((nuint)length * (nuint)sizeof(T)), 0x3000 /* COMMIT | RESERVE */, 0x04 /* READWRITE */));
        if (IsInvalid) throw new OutOfMemoryException("Cannot allocate audio ring.");
    }

    public void CopyFrom(ReadOnlySpan<T> source, int offset)
    {
        source.CopyTo(new Span<T>((void*)handle, _length).Slice(offset, source.Length));
        GC.KeepAlive(this);
    }

    public void CopyTo(int offset, Span<T> destination)
    {
        new ReadOnlySpan<T>((void*)handle, _length).Slice(offset, destination.Length).CopyTo(destination);
        GC.KeepAlive(this);
    }

    protected override bool ReleaseHandle() => AudioRingAllocation.VirtualFree(handle, 0, 0x8000 /* RELEASE */);
}

internal static partial class AudioRingAllocation
{
    [LibraryImport("kernel32", SetLastError = true)]
    internal static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFree(nint address, nuint size, uint freeType);
}
