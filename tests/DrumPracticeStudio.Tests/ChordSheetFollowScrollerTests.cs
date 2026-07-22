using System.Windows;
using System.Windows.Controls;
using DrumPracticeStudio.Views;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ChordSheetFollowScrollerTests
{
    [STATestMethod]
    public void ScrollToTop_PlacesEverySuccessiveMarkerAtTheStartOfTheViewer()
    {
        var items = Enumerable.Range(0, 74)
            .Select(index => $"Línea {index}")
            .ToArray();
        var listBox = new ListBox
        {
            ItemsSource = items,
            Height = 620d,
            Width = 420d
        };
        ScrollViewer.SetVerticalScrollBarVisibility(listBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(listBox, false);
        var window = new Window
        {
            Content = listBox,
            Width = 440d,
            Height = 660d,
            Left = -10_000d,
            Top = -10_000d,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            ChordSheetFollowScroller.ReserveViewportTail(listBox);
            window.UpdateLayout();

            var firstContainer = (ListBoxItem?)listBox.ItemContainerGenerator
                .ContainerFromItem(items[0]);
            Assert.IsNotNull(firstContainer);
            Assert.IsTrue(firstContainer.ActualHeight > 0d);
            Assert.IsTrue(firstContainer.IsVisible);

            var scrollViewer = ChordSheetFollowScroller
                .FindVisualChild<ScrollViewer>(listBox);
            Assert.IsNotNull(scrollViewer);
            foreach (var index in new[] { 17, 30, 43, 61, 17 })
            {
                Assert.IsTrue(ChordSheetFollowScroller.ScrollToTop(listBox, items[index]));

                var container = (ListBoxItem?)listBox.ItemContainerGenerator
                    .ContainerFromItem(items[index]);
                Assert.IsNotNull(container);
                var relativeTop = container
                    .TransformToAncestor(scrollViewer)
                    .Transform(new Point(0d, 0d))
                    .Y;
                Assert.AreEqual(
                    0d,
                    relativeTop,
                    1.5d,
                    $"La línea {index} no quedó arriba.");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
