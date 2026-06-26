using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.Foundation;
using ZLinq;

namespace WinUIMusicPlayer.Helper.Animations;

public static class VisualTreeHelperExtensions
{
    /// <summary>
    /// Gets the implementation root of the Control.
    /// </summary>
    /// <param name="dependencyObject">The DependencyObject.</param>
    /// <returns>Returns the implementation root or null.</returns>
    public static FrameworkElement? GetImplementationRoot(DependencyObject dependencyObject)
    {
        return VisualTreeHelper.GetChildrenCount(dependencyObject) == 1
            ? VisualTreeHelper.GetChild(dependencyObject, 0) as FrameworkElement
            : null;
    }

    extension(Control control)
    {
        public VisualStateGroup? GetVisualStateGroup(string groupName)
        {
            if (
                GetImplementationRoot(control) is FrameworkElement f
                && VisualStateManager.GetVisualStateGroups(f) is IList<VisualStateGroup> groups
            )
            {
                return groups.AsValueEnumerable().FirstOrDefault(g => g.Name == groupName);
            }
            return null;
        }
    }

    extension(FrameworkElement dob)
    {
        /// <summary>
        /// Gets the bounding rectangle of a given element
        /// relative to a given other element or visual root
        /// if relativeTo is null or not specified.
        /// </summary>
        /// <param name="dob">The starting element.</param>
        /// <param name="relativeTo">The relative to element.</param>
        /// <returns></returns>
        /// <exception cref="System.InvalidOperationException">Element not in visual tree.</exception>
        public Rect GetBoundingRect(FrameworkElement relativeTo)
        {
            if (dob == relativeTo)
            {
                return new Rect(0, 0, relativeTo.ActualWidth, relativeTo.ActualHeight);
            }

            var pos = dob.TransformToVisual(relativeTo).TransformPoint(new Point());
            var pos2 = dob.TransformToVisual(relativeTo)
                .TransformPoint(new Point(dob.ActualWidth, dob.ActualHeight));

            return new Rect(pos, pos2);
        }
    }

    extension<T>(FrameworkElement start)
        where T : FrameworkElement
    {
        /// <summary>
        /// Gets the first descendant that is of the given type.
        /// </summary>
        /// <remarks>
        /// Returns null if not found.
        /// </remarks>
        /// <typeparam name="T">Type of descendant to look for.</typeparam>
        /// <param name="start">The start object.</param>
        /// <returns></returns>
        public T? GetFirstDescendantOfType(string name)
        {
            return start.FindDescendants().AsValueEnumerable().OfType<T>().FirstOrDefault(e => e.Name == name);
        }
    }

    extension<T>(DependencyObject start)
        where T : DependencyObject
    {
        /// <summary>
        /// Gets the first descendant that is of the given type.
        /// </summary>
        /// <remarks>
        /// Returns null if not found.
        /// </remarks>
        /// <typeparam name="T">Type of descendant to look for.</typeparam>
        /// <param name="start">The start object.</param>
        /// <returns></returns>
        public T? GetFirstDescendantOfType(Func<T, bool>? predicate = null)
        {
            if (predicate is null)
            {
                return start.FindDescendant<T>();
            }
            else
            {
                return start.FindDescendants().AsValueEnumerable().OfType<T>().FirstOrDefault(predicate);
            }
        }
    }

    extension(DependencyObject start)
    {
        /// <summary>
        /// BFS 查找首个 Name 与 <paramref name="name"/> 匹配的 FrameworkElement。
        /// 零迭代器/闭包分配（仅 BFS Queue 一次性分配）。
        /// 名字比较为 ordinal。
        /// </summary>
        public FrameworkElement? FindDescendantByName(ReadOnlySpan<char> name)
        {
            if (name.IsEmpty)
            {
                return null;
            }

            var queue = new Queue<DependencyObject>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                int count = VisualTreeHelper.GetChildrenCount(node);

                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);

                    if (child is FrameworkElement fe && fe.Name.AsSpan().SequenceEqual(name))
                    {
                        return fe;
                    }

                    queue.Enqueue(child);
                }
            }

            return null;
        }

        /// <summary>
        /// BFS 查找首个 Name 与 <paramref name="name"/> 匹配且类型为 <typeparamref name="T"/> 的元素。
        /// 零迭代器/闭包分配（仅 BFS Queue 一次性分配）。
        /// </summary>
        public T? FindDescendantByName<T>(ReadOnlySpan<char> name)
            where T : DependencyObject
        {
            if (name.IsEmpty)
            {
                return null;
            }

            var queue = new Queue<DependencyObject>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                int count = VisualTreeHelper.GetChildrenCount(node);

                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);

                    if (child is T t && child is FrameworkElement fe && fe.Name.AsSpan().SequenceEqual(name))
                    {
                        return t;
                    }

                    queue.Enqueue(child);
                }
            }

            return null;
        }
    }

    extension<T>(FrameworkElement start)
        where T : FrameworkElement
    {
        public IEnumerable<T> GetFirstLevelDescendantsOfType(Predicate<T>? predicate = null)
        {
            var queue = new Queue<FrameworkElement>();
            var count = VisualTreeHelper.GetChildrenCount(start);

            for (var i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(start, i) is FrameworkElement child)
                {
                    if (child is T c && (predicate == null || predicate(c)))
                    {
                        yield return c;
                        continue;
                    }
                    else
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                var count2 = VisualTreeHelper.GetChildrenCount(parent);

                for (var i = 0; i < count2; i++)
                {
                    if (VisualTreeHelper.GetChild(parent, i) is FrameworkElement child)
                    {
                        if (child is T c && (predicate == null || predicate(c)))
                        {
                            yield return c;
                            continue;
                        }
                        else
                        {
                            queue.Enqueue(child);
                        }
                    }
                }
            }
        }
    }

    extension(UIElement? element)
    {
        public bool ContainsFocus()
        {
            if (element == null)
            {
                return false;
            }

            if (FocusManager.GetFocusedElement() is not UIElement focused)
            {
                return false;
            }

            if (focused == element)
            {
                return true;
            }

            return focused.FindAscendants().AsValueEnumerable().Any(a => a == element);
        }
    }
}
