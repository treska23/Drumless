using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrumPracticeStudio.Views;

internal static class ChordSheetFollowScroller
{
    public static void ReserveViewportTail(ListBox listBox)
    {
        var viewportHeight = Math.Max(0d, listBox.ActualHeight);
        var itemsPresenter = FindVisualChild<ItemsPresenter>(listBox);
        if (itemsPresenter is null)
        {
            return;
        }
        itemsPresenter.Margin = new Thickness(
            itemsPresenter.Margin.Left,
            itemsPresenter.Margin.Top,
            itemsPresenter.Margin.Right,
            viewportHeight);
    }

    public static bool ScrollToTop(ListBox listBox, object item)
    {
        ReserveViewportTail(listBox);
        listBox.ScrollIntoView(item);
        listBox.UpdateLayout();

        var container = listBox.ItemContainerGenerator.ContainerFromItem(item)
            as ListBoxItem;
        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (container is null || scrollViewer is null)
        {
            return false;
        }

        var relativeTop = container
            .TransformToAncestor(scrollViewer)
            .Transform(new Point(0d, 0d))
            .Y;
        scrollViewer.ScrollToVerticalOffset(
            Math.Max(0d, scrollViewer.VerticalOffset + relativeTop));
        listBox.UpdateLayout();
        return true;
    }

    internal static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }
        return null;
    }
}
