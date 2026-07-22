using System.Windows;
using System.Windows.Controls;
using DrumPracticeStudio.Views;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ChordSheetFollowScrollerTests
{
    [STATestMethod]
    public void ScrollToTop_PlacesMarkedLineAtTheStartOfTheViewer()
    {
        var items = Enumerable.Range(0, 80)
            .Select(index => $"Línea {index}")
            .ToArray();
        var listBox = new ListBox
        {
            ItemsSource = items,
            Height = 180d,
            Width = 420d
        };
        ScrollViewer.SetVerticalScrollBarVisibility(listBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(listBox, false);
        var window = new Window
        {
            Content = listBox,
            Width = 440d,
            Height = 220d,
            Left = -10_000d,
            Top = -10_000d,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.IsTrue(ChordSheetFollowScroller.ScrollToTop(listBox, items[45]));

            var container = (ListBoxItem?)listBox.ItemContainerGenerator
                .ContainerFromItem(items[45]);
            var scrollViewer = ChordSheetFollowScroller
                .FindVisualChild<ScrollViewer>(listBox);
            Assert.IsNotNull(container);
            Assert.IsNotNull(scrollViewer);
            var relativeTop = container
                .TransformToAncestor(scrollViewer)
                .Transform(new Point(0d, 0d))
                .Y;
            Assert.AreEqual(0d, relativeTop, 1.5d);
            Assert.IsTrue(scrollViewer.VerticalOffset > 0d);
        }
        finally
        {
            window.Close();
        }
    }
}
