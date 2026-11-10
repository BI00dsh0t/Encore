using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Encore.App;

/// <summary>Walks WPF's element trees: the visual tree, or the logical tree where an element isn't a visual.</summary>
internal static class VisualTreeSearch
{
    /// <summary>Whether the element is the ancestor or lies inside it.</summary>
    public static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the element is, or lies inside, a <typeparamref name="T"/> that is below <paramref name="top"/>.</summary>
    public static bool IsInside<T>(DependencyObject element, DependencyObject top)
        where T : DependencyObject
    {
        for (var current = element; current is not null && !ReferenceEquals(current, top); current = ParentOf(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first <typeparamref name="T"/> below the root, searched depth first; null when there is none.</summary>
    public static T? Descendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? Descendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
