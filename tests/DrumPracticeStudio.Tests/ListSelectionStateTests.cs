using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Views;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ListSelectionStateTests
{
    [STATestMethod]
    public void Preserve_AfterRebuildKeepsSurvivingSelectionAndScrollPosition()
    {
        var items = new ObservableCollection<Row>(Enumerable.Range(0, 100)
            .Select(index => new Row(index.ToString())));
        var list = new ListBox
        {
            ItemsSource = items,
            SelectionMode = SelectionMode.Extended,
            Width = 400,
            Height = 300
        };
        ScrollViewer.SetCanContentScroll(list, false);
        var window = new Window
        {
            Content = list,
            Width = 440,
            Height = 340,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            list.SelectedItems.Add(items[22]);
            list.SelectedItems.Add(items[20]);
            list.SelectedItems.Add(items[21]);
            var scroll = ChordSheetFollowScroller.FindVisualChild<ScrollViewer>(list);
            Assert.IsNotNull(scroll);
            scroll.ScrollToVerticalOffset(300);
            window.UpdateLayout();
            var offset = scroll.VerticalOffset;
            Assert.IsTrue(offset > 0);

            ListSelectionState.Preserve<Row>(list, row => row.Id, () =>
            {
                items.Clear();
                foreach (var index in Enumerable.Range(0, 100).Where(index => index != 21))
                {
                    items.Add(new Row(index.ToString()));
                }
            });
            DrainDispatcher();
            window.UpdateLayout();

            CollectionAssert.AreEqual(new[] { "20", "22" },
                list.SelectedItems.Cast<Row>().Select(row => row.Id).ToArray());
            Assert.AreEqual(offset, scroll.VerticalOffset, 1d,
                "Reconstruir la lista no debe devolver el scroll al principio.");
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public void Preserve_DoesNotRestoreAnOldSelectionAfterChangingContext()
    {
        var first = new Row("first");
        var next = new Row("next");
        var list = new ListBox { ItemsSource = new[] { first, next }, SelectedItem = first };
        var sameContext = true;

        ListSelectionState.Preserve<Row>(list, row => row.Id,
            () => list.SelectedItem = next, () => sameContext);
        sameContext = false;
        DrainDispatcher();

        Assert.AreSame(next, list.SelectedItem);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [STATestMethod]
    public void Preserve_KeepsTheNormalPlaybackSelectionBindingActive()
    {
        var first = new Row("first");
        var next = new Row("next");
        var source = new ListBox { ItemsSource = new[] { first, next }, SelectedItem = first };
        var list = new ListBox { ItemsSource = new[] { first, next } };
        list.SetBinding(Selector.SelectedItemProperty,
            new Binding(nameof(ListBox.SelectedItem)) { Source = source, Mode = BindingMode.TwoWay });

        ListSelectionState.Preserve<Row>(list, row => row.Id,
            () => list.ItemsSource = new[] { first, next });
        DrainDispatcher();

        Assert.IsTrue(BindingOperations.IsDataBound(list, Selector.SelectedItemProperty));
        source.SelectedItem = next;
        DrainDispatcher();
        Assert.AreSame(next, list.SelectedItem,
            "La selección externa de reproducción debe seguir actualizando la lista.");
    }

    private sealed record Row(string Id);
}
