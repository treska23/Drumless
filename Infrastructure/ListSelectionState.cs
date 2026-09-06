using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace DrumPracticeStudio.Infrastructure;

public static class ListSelectionState
{
    public static void Preserve<T>(
        ListBox list,
        Func<T, string> getId,
        Action mutation,
        Func<bool>? canRestore = null)
    {
        var selectedIds = list.SelectedItems.OfType<T>()
            .Select(getId).ToHashSet(StringComparer.Ordinal);
        var scroll = FindScrollViewer(list);
        var verticalOffset = scroll?.VerticalOffset ?? 0;
        var horizontalOffset = scroll?.HorizontalOffset ?? 0;

        mutation();

        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (canRestore is not null && !canRestore())
            {
                return;
            }

            var selectedItems = list.Items.OfType<T>()
                .Where(item => selectedIds.Contains(getId(item))).ToArray();
            list.UnselectAll();
            if (list.SelectionMode == SelectionMode.Single)
            {
                list.SetCurrentValue(Selector.SelectedItemProperty, selectedItems.FirstOrDefault());
            }
            else
            {
                foreach (var item in selectedItems)
                {
                    list.SelectedItems.Add(item);
                }
            }

            list.UpdateLayout();
            scroll = FindScrollViewer(list);
            scroll?.ScrollToVerticalOffset(verticalOffset);
            scroll?.ScrollToHorizontalOffset(horizontalOffset);
        }));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroll)
            {
                return scroll;
            }
            if (FindScrollViewer(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
