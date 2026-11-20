using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WinUIMusicPlayer.Model;

internal static class ObservableListUpdates
{
    // Inputs contain unique row instances. Keep common rows and emit individual notifications
    // so WinUI can retain its realized items, selection and scroll anchor.
    public static void Apply<T>(ObservableCollection<T> target, ReadOnlySpan<T> desired) where T : class
    {
        int start = 0;
        int shared = Math.Min(target.Count, desired.Length);
        while (start < shared && ReferenceEquals(target[start], desired[start])) start++;
        if (start == target.Count && start == desired.Length) return;

        int oldEnd = target.Count;
        int newEnd = desired.Length;
        while (oldEnd > start && newEnd > start && ReferenceEquals(target[oldEnd - 1], desired[newEnd - 1]))
        {
            oldEnd--;
            newEnd--;
        }

        if (oldEnd - start == 1 && newEnd - start == 1)
        {
            target[start] = desired[start];
            return;
        }

        if (start == oldEnd)
        {
            for (int i = start; i < newEnd; i++) target.Insert(i, desired[i]);
            return;
        }
        if (start == newEnd)
        {
            for (int i = oldEnd - 1; i >= start; i--) target.RemoveAt(i);
            return;
        }

        var wanted = new HashSet<T>(ReferenceEqualityComparer.Instance);
        for (int i = start; i < newEnd; i++) wanted.Add(desired[i]);
        var retained = new HashSet<T>(ReferenceEqualityComparer.Instance);
        for (int i = oldEnd - 1; i >= start; i--)
        {
            var item = target[i];
            if (wanted.Contains(item)) retained.Add(item);
            else target.RemoveAt(i);
        }

        for (int i = start; i < newEnd; i++)
        {
            var item = desired[i];
            if (!retained.Contains(item))
            {
                target.Insert(i, item);
                continue;
            }
            if (ReferenceEquals(target[i], item)) continue;
            // A surviving row can change relative order, e.g. when its sort key was edited in place.
            int previous = i + 1;
            while (!ReferenceEquals(target[previous], item)) previous++;
            target.Move(previous, i);
        }
    }
}
